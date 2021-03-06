// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.AspNet.Razor.Chunks;
using Microsoft.AspNet.Razor.CodeGenerators.Visitors;
using Microsoft.AspNet.Razor.TagHelpers;
using Microsoft.Framework.Internal;

namespace Microsoft.AspNet.Razor.CodeGenerators
{
    /// <summary>
    /// Renders tag helper rendering code.
    /// </summary>
    public class CSharpTagHelperCodeRenderer
    {
        internal static readonly string ExecutionContextVariableName = "__tagHelperExecutionContext";
        internal static readonly string StringValueBufferVariableName = "__tagHelperStringValueBuffer";
        internal static readonly string ScopeManagerVariableName = "__tagHelperScopeManager";
        internal static readonly string RunnerVariableName = "__tagHelperRunner";

        private readonly CSharpCodeWriter _writer;
        private readonly CodeGeneratorContext _context;
        private readonly IChunkVisitor _bodyVisitor;
        private readonly IChunkVisitor _literalBodyVisitor;
        private readonly GeneratedTagHelperContext _tagHelperContext;
        private readonly bool _designTimeMode;

        /// <summary>
        /// Instantiates a new <see cref="CSharpTagHelperCodeRenderer"/>.
        /// </summary>
        /// <param name="bodyVisitor">The <see cref="IChunkVisitor"/> used to render chunks found in the body.</param>
        /// <param name="writer">The <see cref="CSharpCodeWriter"/> used to write code.</param>
        /// <param name="context">A <see cref="CodeGeneratorContext"/> instance that contains information about
        /// the current code generation process.</param>
        public CSharpTagHelperCodeRenderer(
            [NotNull] IChunkVisitor bodyVisitor,
            [NotNull] CSharpCodeWriter writer,
            [NotNull] CodeGeneratorContext context)
        {
            _bodyVisitor = bodyVisitor;
            _writer = writer;
            _context = context;
            _tagHelperContext = context.Host.GeneratedClassContext.GeneratedTagHelperContext;
            _designTimeMode = context.Host.DesignTimeMode;

            _literalBodyVisitor = new CSharpLiteralCodeVisitor(this, writer, context);
            AttributeValueCodeRenderer = new TagHelperAttributeValueCodeRenderer();
        }

        public TagHelperAttributeValueCodeRenderer AttributeValueCodeRenderer { get; set; }

        /// <summary>
        /// Renders the code for the given <paramref name="chunk"/>.
        /// </summary>
        /// <param name="chunk">A <see cref="TagHelperChunk"/> to render.</param>
        public void RenderTagHelper(TagHelperChunk chunk)
        {
            // Remove any duplicate TagHelperDescriptors that reference the same type name. Duplicates can occur when
            // multiple TargetElement attributes are on a TagHelper type and matches overlap for an HTML element.
            // Having more than one descriptor with the same TagHelper type results in generated code that runs
            // the same TagHelper X many times (instead of once) over a single HTML element.
            var tagHelperDescriptors = chunk.Descriptors.Distinct(TypeBasedTagHelperDescriptorComparer.Default);

            RenderBeginTagHelperScope(chunk.TagName, chunk.SelfClosing, chunk.Children);

            RenderTagHelpersCreation(chunk, tagHelperDescriptors);

            // Determine what attributes exist in the element and divide them up.
            var htmlAttributes = chunk.Attributes;
            var attributeDescriptors = tagHelperDescriptors.SelectMany(descriptor => descriptor.Attributes).ToArray();
            var unboundHtmlAttributes = htmlAttributes.Where(attribute => !attributeDescriptors.Any(
                attributeDescriptor => attributeDescriptor.IsNameMatch(attribute.Key)));

            RenderUnboundHTMLAttributes(unboundHtmlAttributes);

            // No need to run anything in design time mode.
            if (!_designTimeMode)
            {
                RenderRunTagHelpers();
                RenderWriteTagHelperMethodCall();
                RenderEndTagHelpersScope();
            }
        }

        internal static string GetVariableName(TagHelperDescriptor descriptor)
        {
            return "__" + descriptor.TypeName.Replace('.', '_');
        }

        private void RenderBeginTagHelperScope(string tagName, bool selfClosing, IList<Chunk> children)
        {
            // Scopes/execution contexts are a runtime feature.
            if (_designTimeMode)
            {
                // Render all of the tag helper children inline for IntelliSense.
                _bodyVisitor.Accept(children);
                return;
            }

            // Call into the tag helper scope manager to start a new tag helper scope.
            // Also capture the value as the current execution context.
            _writer.WriteStartAssignment(ExecutionContextVariableName)
                   .WriteStartInstanceMethodInvocation(ScopeManagerVariableName,
                                                       _tagHelperContext.ScopeManagerBeginMethodName);

            // Assign a unique ID for this instance of the source HTML tag. This must be unique
            // per call site, e.g. if the tag is on the view twice, there should be two IDs.
            _writer.WriteStringLiteral(tagName)
                   .WriteParameterSeparator()
                   .WriteBooleanLiteral(selfClosing)
                   .WriteParameterSeparator()
                   .WriteStringLiteral(GenerateUniqueId())
                   .WriteParameterSeparator();

            // We remove the target writer so TagHelper authors can retrieve content.
            var oldWriter = _context.TargetWriterName;
            _context.TargetWriterName = null;

            // Disabling instrumentation inside TagHelper bodies since we never know if it's accurate
            var oldInstrumentation = _context.Host.EnableInstrumentation;
            _context.Host.EnableInstrumentation = false;

            using (_writer.BuildAsyncLambda(endLine: false))
            {
                // Render all of the tag helper children.
                _bodyVisitor.Accept(children);
            }

            _context.Host.EnableInstrumentation = oldInstrumentation;

            _context.TargetWriterName = oldWriter;

            _writer.WriteParameterSeparator()
                   .Write(_tagHelperContext.StartTagHelperWritingScopeMethodName)
                   .WriteParameterSeparator()
                   .Write(_tagHelperContext.EndTagHelperWritingScopeMethodName)
                   .WriteEndMethodInvocation();
        }

        /// <summary>
        /// Generates a unique ID for an HTML element.
        /// </summary>
        /// <returns>
        /// A globally unique ID.
        /// </returns>
        protected virtual string GenerateUniqueId()
        {
            return Guid.NewGuid().ToString("N");
        }

        private void RenderTagHelpersCreation(
            TagHelperChunk chunk,
            IEnumerable<TagHelperDescriptor> tagHelperDescriptors)
        {
            // This is to maintain value accessors for attributes when creating the TagHelpers.
            // Ultimately it enables us to do scenarios like this:
            // myTagHelper1.Foo = DateTime.Now;
            // myTagHelper2.Foo = myTagHelper1.Foo;
            var htmlAttributeValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var tagHelperDescriptor in tagHelperDescriptors)
            {
                var tagHelperVariableName = GetVariableName(tagHelperDescriptor);

                // Create the tag helper
                _writer.WriteStartAssignment(tagHelperVariableName)
                       .WriteStartMethodInvocation(_tagHelperContext.CreateTagHelperMethodName,
                                                   tagHelperDescriptor.TypeName)
                       .WriteEndMethodInvocation();

                // Execution contexts are a runtime feature.
                if (!_designTimeMode)
                {
                    _writer.WriteInstanceMethodInvocation(ExecutionContextVariableName,
                                                          _tagHelperContext.ExecutionContextAddMethodName,
                                                          tagHelperVariableName);
                }

                // Render all of the bound attribute values for the tag helper.
                RenderBoundHTMLAttributes(
                    chunk.Attributes,
                    tagHelperDescriptor.TypeName,
                    tagHelperVariableName,
                    tagHelperDescriptor.Attributes,
                    htmlAttributeValues);
            }
        }

        private void RenderBoundHTMLAttributes(
            IList<KeyValuePair<string, Chunk>> chunkAttributes,
            string tagHelperTypeName,
            string tagHelperVariableName,
            IEnumerable<TagHelperAttributeDescriptor> attributeDescriptors,
            Dictionary<string, string> htmlAttributeValues)
        {
            // Track dictionary properties we have confirmed are non-null.
            var confirmedDictionaries = new HashSet<string>(StringComparer.Ordinal);

            // First attribute wins, even if there are duplicates.
            var distinctAttributes = chunkAttributes.Distinct(KeyValuePairKeyComparer.Default);

            // Go through the HTML attributes in source order, assigning to properties or indexers as we go.
            foreach (var attributeKeyValuePair in distinctAttributes)
            {
                var attributeName = attributeKeyValuePair.Key;
                var attributeValueChunk = attributeKeyValuePair.Value;
                if (attributeValueChunk == null)
                {
                    // Minimized attributes are not valid for bound attributes. TagHelperBlockRewriter has already
                    // logged an error if it was a bound attribute; so we can skip.
                    continue;
                }

                // Find the matching TagHelperAttributeDescriptor.
                var attributeDescriptor = attributeDescriptors.FirstOrDefault(
                    descriptor => descriptor.IsNameMatch(attributeName));
                if (attributeDescriptor == null)
                {
                    // Attribute is not bound to a property or indexer in this tag helper.
                    continue;
                }

                // We capture the tag helper's property value accessor so we can retrieve it later (if we need to).
                var valueAccessor = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.{1}",
                    tagHelperVariableName,
                    attributeDescriptor.PropertyName);

                if (attributeDescriptor.IsIndexer)
                {
                    // Need a different valueAccessor in this case. But first need to throw a reasonable Exception at
                    // runtime if the property is null. The check is not required at design time.
                    if (!_designTimeMode && confirmedDictionaries.Add(attributeDescriptor.PropertyName))
                    {
                        _writer
                            .Write("if (")
                            .Write(valueAccessor)
                            .WriteLine(" == null)");
                        using (_writer.BuildScope())
                        {
                            // System is in Host.NamespaceImports for all MVC scenarios. No need to generate FullName
                            // of InvalidOperationException type.
                            _writer
                                .Write("throw ")
                                .WriteStartNewObject(nameof(InvalidOperationException))
                                .WriteStartMethodInvocation(_tagHelperContext.FormatInvalidIndexerAssignmentMethodName)
                                .WriteStringLiteral(attributeName)
                                .WriteParameterSeparator()
                                .WriteStringLiteral(tagHelperTypeName)
                                .WriteParameterSeparator()
                                .WriteStringLiteral(attributeDescriptor.PropertyName)
                                .WriteEndMethodInvocation(endLine: false)   // End of method call
                                .WriteEndMethodInvocation(endLine: true);   // End of new expression / throw statement
                        }
                    }

                    var dictionaryKey = attributeName.Substring(attributeDescriptor.Name.Length);
                    valueAccessor = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}.{1}[\"{2}\"]",
                        tagHelperVariableName,
                        attributeDescriptor.PropertyName,
                        dictionaryKey);
                }

                // If we haven't recorded this attribute value before then we need to record its value.
                var attributeValueRecorded = htmlAttributeValues.ContainsKey(attributeName);
                if (!attributeValueRecorded)
                {
                    // We only need to create attribute values once per HTML element (not once per tag helper).
                    // We're saving the value accessor so we can retrieve it later if there are more tag
                    // helpers that need the value.
                    htmlAttributeValues.Add(attributeName, valueAccessor);

                    // Bufferable attributes are attributes that can have Razor code inside of them. Such
                    // attributes have string values and may be calculated using a temporary TextWriter or other
                    // buffer.
                    var bufferableAttribute = attributeDescriptor.IsStringProperty;

                    RenderNewAttributeValueAssignment(
                        attributeDescriptor,
                        bufferableAttribute,
                        attributeValueChunk,
                        valueAccessor);

                    // Execution contexts are a runtime feature.
                    if (_designTimeMode)
                    {
                        continue;
                    }

                    // We need to inform the context of the attribute value.
                    _writer
                        .WriteStartInstanceMethodInvocation(
                            ExecutionContextVariableName,
                            _tagHelperContext.ExecutionContextAddTagHelperAttributeMethodName)
                        .WriteStringLiteral(attributeName)
                        .WriteParameterSeparator()
                        .Write(valueAccessor)
                        .WriteEndMethodInvocation();
                }
                else
                {
                    // The attribute value has already been recorded, lets retrieve it from the stored value
                    // accessors.
                    _writer
                        .WriteStartAssignment(valueAccessor)
                        .Write(htmlAttributeValues[attributeName])
                        .WriteLine(";");
                }
            }
        }

        // Render assignment of attribute value to the value accessor.
        private void RenderNewAttributeValueAssignment(
            TagHelperAttributeDescriptor attributeDescriptor,
            bool bufferableAttribute,
            Chunk attributeValueChunk,
            string valueAccessor)
        {
            // Plain text values are non Razor code (@DateTime.Now) values. If an attribute is bufferable it
            // may be more than just a plain text value, it may also contain Razor code which is why we attempt
            // to retrieve a plain text value here.
            string textValue;
            var isPlainTextValue = TryGetPlainTextValue(attributeValueChunk, out textValue);

            if (bufferableAttribute)
            {
                if (!isPlainTextValue)
                {
                    // If we haven't recorded a value and we need to buffer an attribute value and the value is not
                    // plain text then we need to prepare the value prior to setting it below.
                    BuildBufferedWritingScope(attributeValueChunk, htmlEncodeValues: false);
                }

                _writer.WriteStartAssignment(valueAccessor);

                if (isPlainTextValue)
                {
                    // If the attribute is bufferable but has a plain text value that means the value
                    // is a string which needs to be surrounded in quotes.
                    RenderQuotedAttributeValue(textValue, attributeDescriptor);
                }
                else
                {
                    // The value contains more than plain text e.g. stringAttribute ="Time: @DateTime.Now".
                    RenderBufferedAttributeValue(attributeDescriptor);
                }

                _writer.WriteLine(";");
            }
            else
            {
                // Write out simple assignment for non-string property value. Try to keep the whole
                // statement together and the #line pragma correct to make debugging possible.
                using (var lineMapper = new CSharpLineMappingWriter(
                    _writer,
                    attributeValueChunk.Association.Start,
                    _context.SourceFile))
                {
                    // Place the assignment LHS to align RHS with original attribute value's indentation.
                    // Unfortunately originalIndent is incorrect if original line contains tabs. Unable to
                    // use a CSharpPaddingBuilder because the Association has no Previous node; lost the
                    // original Span sequence when the parse tree was rewritten.
                    var originalIndent = attributeValueChunk.Start.CharacterIndex;
                    var generatedLength = valueAccessor.Length + " = ".Length;
                    var newIndent = originalIndent - generatedLength;
                    if (newIndent > 0)
                    {
                        _writer.Indent(newIndent);
                    }

                    _writer.WriteStartAssignment(valueAccessor);
                    lineMapper.MarkLineMappingStart();

                    // Write out bare expression for this attribute value. Property is not a string.
                    // So quoting or buffering are not helpful.
                    RenderRawAttributeValue(attributeValueChunk, attributeDescriptor, isPlainTextValue);

                    // End the assignment to the attribute.
                    lineMapper.MarkLineMappingEnd();
                    _writer.WriteLine(";");
                }
            }
        }

        private void RenderUnboundHTMLAttributes(IEnumerable<KeyValuePair<string, Chunk>> unboundHTMLAttributes)
        {
            // Build out the unbound HTML attributes for the tag builder
            foreach (var htmlAttribute in unboundHTMLAttributes)
            {
                string textValue = null;
                var isPlainTextValue = false;
                var attributeValue = htmlAttribute.Value;

                // A null attribute value means the HTML attribute is minimized.
                if (attributeValue != null)
                {
                    isPlainTextValue = TryGetPlainTextValue(attributeValue, out textValue);

                    // HTML attributes are always strings. So if this value is not plain text i.e. if the value contains
                    // C# code, then we need to buffer it.
                    if (!isPlainTextValue)
                    {
                        BuildBufferedWritingScope(attributeValue, htmlEncodeValues: true);
                    }
                }

                // Execution contexts are a runtime feature, therefore no need to add anything to them.
                if (_designTimeMode)
                {
                    continue;
                }

                // If we have a minimized attribute there is no value
                if (attributeValue == null)
                {
                    _writer
                        .WriteStartInstanceMethodInvocation(
                            ExecutionContextVariableName,
                            _tagHelperContext.ExecutionContextAddMinimizedHtmlAttributeMethodName)
                        .WriteStringLiteral(htmlAttribute.Key)
                        .WriteEndMethodInvocation();
                }
                else
                {
                    _writer
                        .WriteStartInstanceMethodInvocation(
                            ExecutionContextVariableName,
                            _tagHelperContext.ExecutionContextAddHtmlAttributeMethodName)
                        .WriteStringLiteral(htmlAttribute.Key)
                        .WriteParameterSeparator()
                        .WriteStartMethodInvocation(_tagHelperContext.MarkAsHtmlEncodedMethodName);

                    // If it's a plain text value then we need to surround the value with quotes.
                    if (isPlainTextValue)
                    {
                        _writer.WriteStringLiteral(textValue);
                    }
                    else
                    {
                        RenderBufferedAttributeValueAccessor(_writer);
                    }

                    _writer.WriteEndMethodInvocation(endLine: false)
                        .WriteEndMethodInvocation();
                }
            }
        }

        private void RenderEndTagHelpersScope()
        {
            _writer.WriteStartAssignment(ExecutionContextVariableName)
                   .WriteInstanceMethodInvocation(ScopeManagerVariableName,
                                                  _tagHelperContext.ScopeManagerEndMethodName);
        }

        private void RenderWriteTagHelperMethodCall()
        {
            _writer.Write("await ");

            if (!string.IsNullOrEmpty(_context.TargetWriterName))
            {
                _writer
                    .WriteStartMethodInvocation(_tagHelperContext.WriteTagHelperToAsyncMethodName)
                    .Write(_context.TargetWriterName)
                    .WriteParameterSeparator();
            }
            else
            {
                _writer.WriteStartMethodInvocation(_tagHelperContext.WriteTagHelperAsyncMethodName);
            }

            _writer
                .Write(ExecutionContextVariableName)
                .WriteEndMethodInvocation();
        }

        private void RenderRunTagHelpers()
        {
            _writer.Write(ExecutionContextVariableName)
                   .Write(".")
                   .WriteStartAssignment(_tagHelperContext.ExecutionContextOutputPropertyName)
                   .Write("await ")
                   .WriteStartInstanceMethodInvocation(RunnerVariableName,
                                                       _tagHelperContext.RunnerRunAsyncMethodName);

            _writer.Write(ExecutionContextVariableName)
                   .WriteEndMethodInvocation();
        }

        private void RenderBufferedAttributeValue(TagHelperAttributeDescriptor attributeDescriptor)
        {
            // Pass complexValue: false because variable.ToString() replaces any original complexity in the expression.
            RenderAttributeValue(
                attributeDescriptor,
                valueRenderer: (writer) =>
                {
                    RenderBufferedAttributeValueAccessor(writer);
                },
                complexValue: false);
        }

        private void RenderRawAttributeValue(
            Chunk attributeValueChunk,
            TagHelperAttributeDescriptor attributeDescriptor,
            bool isPlainTextValue)
        {
            RenderAttributeValue(
                attributeDescriptor,
                valueRenderer: (writer) =>
                {
                    var visitor =
                        new CSharpTagHelperAttributeValueVisitor(writer, _context, attributeDescriptor.TypeName);
                    visitor.Accept(attributeValueChunk);
                },
                complexValue: !isPlainTextValue);
        }

        private void RenderQuotedAttributeValue(string value, TagHelperAttributeDescriptor attributeDescriptor)
        {
            RenderAttributeValue(
                attributeDescriptor,
                valueRenderer: (writer) =>
                {
                    writer.WriteStringLiteral(value);
                },
                complexValue: false);
        }

        // Render a buffered writing scope for the HTML attribute value.
        private void BuildBufferedWritingScope(Chunk htmlAttributeChunk, bool htmlEncodeValues)
        {
            // We're building a writing scope around the provided chunks which captures everything written from the
            // page. Therefore, we do not want to write to any other buffer since we're using the pages buffer to
            // ensure we capture all content that's written, directly or indirectly.
            var oldWriter = _context.TargetWriterName;
            _context.TargetWriterName = null;

            // Need to disable instrumentation inside of writing scopes, the instrumentation will not detect
            // content written inside writing scopes.
            var oldInstrumentation = _context.Host.EnableInstrumentation;

            try
            {
                _context.Host.EnableInstrumentation = false;

                // Scopes are a runtime feature.
                if (!_designTimeMode)
                {
                    _writer.WriteMethodInvocation(_tagHelperContext.StartTagHelperWritingScopeMethodName);
                }

                var visitor = htmlEncodeValues ? _bodyVisitor : _literalBodyVisitor;
                visitor.Accept(htmlAttributeChunk);

                // Scopes are a runtime feature.
                if (!_designTimeMode)
                {
                    _writer.WriteStartAssignment(StringValueBufferVariableName)
                           .WriteMethodInvocation(_tagHelperContext.EndTagHelperWritingScopeMethodName);
                }
            }
            finally
            {
                // Reset instrumentation back to what it was, leaving the writing scope.
                _context.Host.EnableInstrumentation = oldInstrumentation;

                // Reset the writer/buffer back to what it was, leaving the writing scope.
                _context.TargetWriterName = oldWriter;
            }
        }

        private void RenderAttributeValue(TagHelperAttributeDescriptor attributeDescriptor,
                                          Action<CSharpCodeWriter> valueRenderer,
                                          bool complexValue)
        {
            AttributeValueCodeRenderer.RenderAttributeValue(
                attributeDescriptor,
                _writer,
                _context,
                valueRenderer,
                complexValue);
        }

        private void RenderBufferedAttributeValueAccessor(CSharpCodeWriter writer)
        {
            if (_designTimeMode)
            {
                // There is no value buffer in design time mode but we still want to write out a value. We write a
                // value to ensure the tag helper's property type is string.
                writer.Write("string.Empty");
            }
            else
            {
                writer.WriteInstanceMethodInvocation(StringValueBufferVariableName,
                                                     "ToString",
                                                     endLine: false);
            }
        }

        private static bool TryGetPlainTextValue(Chunk chunk, out string plainText)
        {
            var parentChunk = chunk as ParentChunk;

            plainText = null;

            if (parentChunk == null || parentChunk.Children.Count != 1)
            {
                return false;
            }

            var literalChildChunk = parentChunk.Children[0] as LiteralChunk;

            if (literalChildChunk == null)
            {
                return false;
            }

            plainText = literalChildChunk.Text;

            return true;
        }

        // An IEqualityComparer for string -> Chunk mappings which compares only the Key.
        private class KeyValuePairKeyComparer : IEqualityComparer<KeyValuePair<string, Chunk>>
        {
            public static KeyValuePairKeyComparer Default = new KeyValuePairKeyComparer();

            private KeyValuePairKeyComparer()
            {
            }

            public bool Equals(KeyValuePair<string, Chunk> keyValuePairX, KeyValuePair<string, Chunk> keyValuePairY)
            {
                return string.Equals(keyValuePairX.Key, keyValuePairY.Key, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(KeyValuePair<string, Chunk> keyValuePair)
            {
                return keyValuePair.Key.GetHashCode();
            }
        }

        // A CSharpCodeVisitor which does not HTML encode values. Used when rendering bound string attribute values.
        private class CSharpLiteralCodeVisitor : CSharpCodeVisitor
        {
            public CSharpLiteralCodeVisitor(
                CSharpTagHelperCodeRenderer tagHelperRenderer,
                CSharpCodeWriter writer,
                CodeGeneratorContext context)
                : base(writer, context)
            {
                // Ensure that no matter how this class is used, we don't create numerous CSharpTagHelperCodeRenderer
                // instances.
                TagHelperRenderer = tagHelperRenderer;
            }

            protected override string WriteMethodName
            {
                get
                {
                    return Context.Host.GeneratedClassContext.WriteLiteralMethodName;
                }
            }

            protected override string WriteToMethodName
            {
                get
                {
                    return Context.Host.GeneratedClassContext.WriteLiteralToMethodName;
                }
            }
        }
    }
}