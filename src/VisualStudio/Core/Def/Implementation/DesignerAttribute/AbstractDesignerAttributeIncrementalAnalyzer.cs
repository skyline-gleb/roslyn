// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editor;
using Microsoft.CodeAnalysis.Editor.Shared.Options;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Versions;
using Microsoft.VisualStudio.Designer.Interfaces;
using Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem;
using Microsoft.VisualStudio.Shell.Interop;
using Roslyn.Utilities;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.DesignerAttribute
{
    internal abstract partial class AbstractDesignerAttributeIncrementalAnalyzer : ForegroundThreadAffinitizedObject
    {
        private readonly IOptionService _optionService;
        private readonly IForegroundNotificationService _notificationService;

        private readonly IServiceProvider _serviceProvider;
        private readonly DesignerAttributeState _state;
        private readonly IAsynchronousOperationListener _listener;

        /// <summary>
        /// cache designer from UI thread
        /// 
        /// access this field through <see cref="GetDesignerFromForegroundThread"/>
        /// </summary>
        private IVSMDDesignerService _dotNotAccessDirectlyDesigner;

        public AbstractDesignerAttributeIncrementalAnalyzer(
            IServiceProvider serviceProvider,
            IOptionService optionService,
            IForegroundNotificationService notificationService,
            IEnumerable<Lazy<IAsynchronousOperationListener, FeatureMetadata>> asyncListeners)
        {
            _serviceProvider = serviceProvider;
            Contract.ThrowIfNull(_serviceProvider);

            _optionService = optionService;
            _notificationService = notificationService;

            _listener = new AggregateAsynchronousOperationListener(asyncListeners, FeatureAttribute.DesignerAttribute);
            _state = new DesignerAttributeState();
        }

        protected abstract bool ProcessOnlyFirstTypeDefined();
        protected abstract IEnumerable<SyntaxNode> GetAllTopLevelTypeDefined(SyntaxNode root);
        protected abstract bool HasAttributesOrBaseTypeOrIsPartial(SyntaxNode typeNode);

        public System.Threading.Tasks.Task DocumentResetAsync(Document document, CancellationToken cancellationToken)
        {
            _state.Remove(document.Id);
            return _state.PersistAsync(document, new Data(VersionStamp.Default, VersionStamp.Default, designerAttributeArgument: string.Empty), cancellationToken);
        }

        public bool NeedsReanalysisOnOptionChanged(object sender, OptionChangedEventArgs e)
        {
            return false;
        }

        public async System.Threading.Tasks.Task AnalyzeDocumentAsync(Document document, SyntaxNode bodyOpt, CancellationToken cancellationToken)
        {
            Contract.ThrowIfFalse(document.IsFromPrimaryBranch());

            cancellationToken.ThrowIfCancellationRequested();

            if (!_optionService.GetOption(InternalFeatureOnOffOptions.DesignerAttributes))
            {
                return;
            }

            // use tree version so that things like compiler option changes are considered
            var textVersion = await document.GetTextVersionAsync(cancellationToken).ConfigureAwait(false);
            var projectVersion = await document.Project.GetDependentVersionAsync(cancellationToken).ConfigureAwait(false);
            var semanticVersion = await document.Project.GetDependentSemanticVersionAsync(cancellationToken).ConfigureAwait(false);

            var existingData = await _state.TryGetExistingDataAsync(document, cancellationToken).ConfigureAwait(false);
            if (existingData != null)
            {
                // check whether we can use the data as it is (can happen when re-using persisted data from previous VS session)
                if (CheckVersions(document, textVersion, projectVersion, semanticVersion, existingData))
                {
                    var workspace = document.Project.Solution.Workspace as VisualStudioWorkspaceImpl;
                    RegisterDesignerAttribute(workspace, document.Id, existingData.DesignerAttributeArgument);
                    return;
                }
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            // Delay getting any of these until we need them, but hold on to them once we have them.
            string designerAttributeArgument = null;
            Compilation compilation = null;
            INamedTypeSymbol designerAttribute = null;
            SemanticModel model = null;

            // get type defined in current tree
            foreach (var typeNode in GetAllTopLevelTypeDefined(root))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (HasAttributesOrBaseTypeOrIsPartial(typeNode))
                {
                    if (designerAttribute == null)
                    {
                        if (compilation == null)
                        {
                            compilation = await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                        }

                        designerAttribute = compilation.DesignerCategoryAttributeType();
                        if (designerAttribute == null)
                        {
                            // The DesignerCategoryAttribute doesn't exist.
                            InvalidateDocument(document);
                            return;
                        }
                    }

                    if (model == null)
                    {
                        model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                    }

                    var definedType = model.GetDeclaredSymbol(typeNode, cancellationToken) as INamedTypeSymbol;
                    if (definedType == null)
                    {
                        continue;
                    }

                    // walk up type chain
                    foreach (var type in definedType.GetBaseTypesAndThis())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // if it has designer attribute, set it
                        var attribute = type.GetAttributes().Where(d => designerAttribute.Equals(d.AttributeClass)).FirstOrDefault();
                        if (attribute != null && attribute.ConstructorArguments.Length == 1)
                        {
                            designerAttributeArgument = GetArgumentString(attribute.ConstructorArguments[0]);
                            await RegisterDesignerAttributeAndSaveStateAsync(document, textVersion, semanticVersion, designerAttributeArgument, existingData, cancellationToken).ConfigureAwait(false);

                            return;
                        }
                    }
                }

                // check only first type
                if (ProcessOnlyFirstTypeDefined())
                {
                    break;
                }
            }

            await RegisterDesignerAttributeAndSaveStateAsync(document, textVersion, semanticVersion, designerAttributeArgument, existingData, cancellationToken).ConfigureAwait(false);
        }

        private bool CheckVersions(
            Document document, VersionStamp textVersion, VersionStamp dependentProjectVersion, VersionStamp dependentSemanticVersion, Data existingData)
        {
            // first check full version to see whether we can reuse data in same session, if we can't, check timestamp only version to see whether
            // we can use it cross-session.
            return document.CanReusePersistedTextVersion(textVersion, existingData.TextVersion) &&
                   document.Project.CanReusePersistedDependentSemanticVersion(dependentProjectVersion, dependentSemanticVersion, existingData.SemanticVersion);
        }

        private static string GetArgumentString(TypedConstant argument)
        {
            if (argument.Type == null ||
                argument.Type.SpecialType != SpecialType.System_String ||
                argument.IsNull)
            {
                return null;
            }

            return ((string)argument.Value).Trim();
        }

        private async System.Threading.Tasks.Task RegisterDesignerAttributeAndSaveStateAsync(
            Document document, VersionStamp textVersion, VersionStamp semanticVersion, string designerAttributeArgument,
            Data existingData, CancellationToken cancellationToken)
        {
            if (existingData != null && string.Equals(existingData.DesignerAttributeArgument, designerAttributeArgument, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var data = new Data(textVersion, semanticVersion, designerAttributeArgument);
            await _state.PersistAsync(document, data, cancellationToken).ConfigureAwait(false);

            var workspace = document.Project.Solution.Workspace as VisualStudioWorkspaceImpl;
            RegisterDesignerAttribute(workspace, document.Id, designerAttributeArgument);
        }

        private void RegisterDesignerAttribute(Document document, string designerAttributeArgument)
        {
            var workspace = document.Project.Solution.Workspace as VisualStudioWorkspaceImpl;
            RegisterDesignerAttribute(workspace, document.Id, designerAttributeArgument);
        }

        private void RegisterDesignerAttribute(VisualStudioWorkspaceImpl workspace, DocumentId documentId, string designerAttributeArgument)
        {
            if (workspace == null)
            {
                return;
            }

            _notificationService.RegisterNotification(() =>
            {
                var vsDocument = workspace.GetHostDocument(documentId);
                if (vsDocument == null)
                {
                    return;
                }

                uint itemId = vsDocument.GetItemId();
                if (itemId == (uint)VSConstants.VSITEMID.Nil)
                {
                    // it is no longer part of the solution
                    return;
                }

                object currentValue;
                if (ErrorHandler.Succeeded(vsDocument.Project.Hierarchy.GetProperty(itemId, (int)__VSHPROPID.VSHPROPID_ItemSubType, out currentValue)))
                {
                    var currentStringValue = string.IsNullOrEmpty(currentValue as string) ? null : (string)currentValue;
                    if (string.Equals(currentStringValue, designerAttributeArgument, StringComparison.OrdinalIgnoreCase))
                    {
                        // PERF: Avoid sending the message if the project system already has the current value.
                        return;
                    }
                }

                try
                {
                    var designer = GetDesignerFromForegroundThread();
                    if (designer != null)
                    {
                        designer.RegisterDesignViewAttribute(vsDocument.Project.Hierarchy, (int)itemId, dwClass: 0, pwszAttributeValue: designerAttributeArgument);
                    }
                }
                catch
                {
                    // DevDiv # 933717
                    // turns out RegisterDesignViewAttribute can throw in certain cases such as a file failed to be checked out by source control
                    // or IVSHierarchy failed to set a property for this project
                    //
                    // just swallow it. don't crash VS.
                }
            }, _listener.BeginAsyncOperation("RegisterDesignerAttribute"));
        }

        private IVSMDDesignerService GetDesignerFromForegroundThread()
        {
            if (_dotNotAccessDirectlyDesigner != null)
            {
                return _dotNotAccessDirectlyDesigner;
            }

            AssertIsForeground();
            _dotNotAccessDirectlyDesigner = _serviceProvider.GetService(typeof(SVSMDDesignerService)) as IVSMDDesignerService;

            return _dotNotAccessDirectlyDesigner;
        }

        private void InvalidateDocument(Document document)
        {
            _state.Remove(document.Id);
            RegisterDesignerAttribute(document, designerAttributeArgument: null);
        }

        public void RemoveDocument(DocumentId documentId)
        {
            _state.Remove(documentId);
        }

        private class Data
        {
            public readonly VersionStamp TextVersion;
            public readonly VersionStamp SemanticVersion;
            public readonly string DesignerAttributeArgument;

            public Data(VersionStamp textVersion, VersionStamp semanticVersion, string designerAttributeArgument)
            {
                this.TextVersion = textVersion;
                this.SemanticVersion = semanticVersion;
                this.DesignerAttributeArgument = designerAttributeArgument;
            }
        }

        #region unused
        public System.Threading.Tasks.Task NewSolutionSnapshotAsync(Solution solution, CancellationToken cancellationToken)
        {
            return SpecializedTasks.EmptyTask;
        }

        public System.Threading.Tasks.Task DocumentOpenAsync(Document document, CancellationToken cancellationToken)
        {
            return SpecializedTasks.EmptyTask;
        }

        public System.Threading.Tasks.Task AnalyzeSyntaxAsync(Document document, CancellationToken cancellationToken)
        {
            return SpecializedTasks.EmptyTask;
        }

        public System.Threading.Tasks.Task AnalyzeProjectAsync(Project project, bool semanticsChanged, CancellationToken cancellationToken)
        {
            return SpecializedTasks.EmptyTask;
        }

        public void RemoveProject(ProjectId projectId)
        {
        }
        #endregion
    }
}
