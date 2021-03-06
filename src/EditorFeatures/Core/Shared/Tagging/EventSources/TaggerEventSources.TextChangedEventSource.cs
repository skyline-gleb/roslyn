// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using Microsoft.CodeAnalysis.Editor.Tagging;
using Microsoft.VisualStudio.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Shared.Tagging
{
    internal partial class TaggerEventSources
    {
        private class TextChangedEventSource : AbstractTaggerEventSource
        {
            private readonly ITextBuffer _subjectBuffer;
            private readonly bool _reportChangedSpans;

            public TextChangedEventSource(ITextBuffer subjectBuffer, TaggerDelay delay, bool reportChangedSpans)
                : base(delay)
            {
                Contract.ThrowIfNull(subjectBuffer);

                _subjectBuffer = subjectBuffer;
                _reportChangedSpans = reportChangedSpans;
            }

            public override string EventKind
            {
                get
                {
                    return PredefinedChangedEventKinds.TextChanged;
                }
            }

            public override void Connect()
            {
                _subjectBuffer.Changed += OnTextBufferChanged;
            }

            public override void Disconnect()
            {
                _subjectBuffer.Changed -= OnTextBufferChanged;
            }

            private void OnTextBufferChanged(object sender, TextContentChangedEventArgs e)
            {
                if (e.Changes.Count == 0)
                {
                    return;
                }

                if (_reportChangedSpans)
                {
                    this.RaiseChanged(e);
                }
                else
                {
                    this.RaiseChanged();
                }
            }
        }
    }
}
