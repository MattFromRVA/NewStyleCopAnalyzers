// Copyright (c) Contributors to the New StyleCop Analyzers project.
// Licensed under the MIT License. See LICENSE in the project root for license information.

#nullable disable

namespace StyleCop.Analyzers.MaintainabilityRules
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Composition;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeActions;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using StyleCop.Analyzers.Helpers;
    using StyleCop.Analyzers.Lightup;

    /// <summary>
    /// Implements a code fix for <see cref="SA1402FileMayOnlyContainASingleType"/>.
    /// </summary>
    /// <remarks>
    /// <para>To fix a violation of this rule, move each type into its own file.</para>
    /// </remarks>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SA1402CodeFixProvider))]
    [Shared]
    internal class SA1402CodeFixProvider : CodeFixProvider
    {
        /// <inheritdoc/>
        public override ImmutableArray<string> FixableDiagnosticIds { get; } =
            ImmutableArray.Create(SA1402FileMayOnlyContainASingleType.DiagnosticId);

        /// <inheritdoc/>
        public override FixAllProvider GetFixAllProvider()
        {
            // The batch fixer can't handle code fixes that create new files
            return null;
        }

        /// <inheritdoc/>
        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            foreach (var diagnostic in context.Diagnostics)
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        MaintainabilityResources.SA1402CodeFix,
                        cancellationToken => GetTransformedSolutionAsync(context.Document, diagnostic, cancellationToken),
                        nameof(SA1402CodeFixProvider)),
                    diagnostic);
            }

            return SpecializedTasks.CompletedTask;
        }

        private static async Task<Solution> GetTransformedSolutionAsync(Document document, Diagnostic diagnostic, CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            SyntaxNode node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            if (!(node is MemberDeclarationSyntax memberDeclarationSyntax))
            {
                return document.Project.Solution;
            }

            DocumentId extractedDocumentId = DocumentId.CreateNewId(document.Project.Id);
            string suffix;
            FileNameHelpers.GetFileNameAndSuffix(document.Name, out suffix);
            var settings = document.Project.AnalyzerOptions.GetStyleCopSettingsInCodeFix(root.SyntaxTree, cancellationToken);
            string extractedDocumentName = FileNameHelpers.GetConventionalFileName(memberDeclarationSyntax, settings.DocumentationRules.FileNamingConvention) + suffix;

            List<SyntaxNode> nodesToRemoveFromExtracted = new List<SyntaxNode>();
            SyntaxNode previous = node;
            for (SyntaxNode current = node.Parent; current != null; previous = current, current = current.Parent)
            {
                foreach (SyntaxNode child in current.ChildNodes())
                {
                    if (child == previous)
                    {
                        continue;
                    }

                    switch (child.Kind())
                    {
                    case SyntaxKind.NamespaceDeclaration:
                    case SyntaxKind.ClassDeclaration:
                    case SyntaxKind.StructDeclaration:
                    case SyntaxKind.InterfaceDeclaration:
                    case SyntaxKind.EnumDeclaration:
                    case SyntaxKind.DelegateDeclaration:
                    case SyntaxKindEx.RecordDeclaration:
                    case SyntaxKindEx.RecordStructDeclaration:
                        nodesToRemoveFromExtracted.Add(child);
                        break;

                    case SyntaxKindEx.FileScopedNamespaceDeclaration:
                        // Only one file-scoped namespace is allowed per syntax tree
                        throw new InvalidOperationException("This location is not reachable");

                    default:
                        break;
                    }
                }
            }

            // Add the new file
            SyntaxNode extractedDocumentNode = root.RemoveNodes(nodesToRemoveFromExtracted, SyntaxRemoveOptions.KeepUnbalancedDirectives);
            Solution updatedSolution = document.Project.Solution.AddDocument(extractedDocumentId, extractedDocumentName, extractedDocumentNode, document.Folders);

            // Make sure to also add the file to linked projects
            foreach (var linkedDocumentId in document.GetLinkedDocumentIds())
            {
                DocumentId linkedExtractedDocumentId = DocumentId.CreateNewId(linkedDocumentId.ProjectId);
                updatedSolution = updatedSolution.AddDocument(linkedExtractedDocumentId, extractedDocumentName, extractedDocumentNode, document.Folders);
            }

            // Remove the type from its original location
            updatedSolution = updatedSolution.WithDocumentSyntaxRoot(document.Id, root.RemoveNode(node, SyntaxRemoveOptions.KeepUnbalancedDirectives));

            updatedSolution = await RemoveUnusedUsingsFromDocumentsAsync(updatedSolution, new[] { document.Id, extractedDocumentId }, cancellationToken).ConfigureAwait(false);

            return updatedSolution;
        }

        private static async Task<Solution> RemoveUnusedUsingsFromDocumentsAsync(Solution solution, IEnumerable<DocumentId> documentIds, CancellationToken cancellationToken)
        {
            foreach (var documentId in documentIds)
            {
                var document = solution.GetDocument(documentId);
                if (document == null)
                {
                    continue;
                }

                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);

                var hasUsings = root.DescendantNodes(descendIntoTrivia: false)
                    .OfType<UsingDirectiveSyntax>()
                    .Any();

                if (!hasUsings)
                {
                    continue;
                }

                if (root is CompilationUnitSyntax cu && HasPreprocessorDirectives(cu))
                {
                    continue;
                }

                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var diagnostics = semanticModel.GetDiagnostics();

                var unusedUsings = diagnostics
                    .Where(d => d.Id == "CS8019")
                    .Select(d => root.FindNode(d.Location.SourceSpan))
                    .OfType<UsingDirectiveSyntax>()
                    .Where(u => !HasRelevantTrivia(u))
                    .ToList();

                if (unusedUsings.Count > 0)
                {
                    // Check for header comments that might need to be preserved
                    var usingDirective = unusedUsings[0];
                    var leadingTrivia = usingDirective.GetLeadingTrivia();
                    var hasLeadingComment = leadingTrivia.Any(t =>
                        !t.IsKind(SyntaxKind.WhitespaceTrivia) &&
                        !t.IsKind(SyntaxKind.EndOfLineTrivia));
                    

                    // create a clean root without the unused usings but preserve the leading comment trivia list


                    var cleanedRoot = root.RemoveNodes(unusedUsings, SyntaxRemoveOptions.KeepUnbalancedDirectives);
                    // Add back the leading trivia to the next node
                    if (hasLeadingComment)
                    {

                        var nextNode = cleanedRoot.DescendantNodes(descendIntoTrivia: false)
                            .OfType<UsingDirectiveSyntax>()
                            .FirstOrDefault();
                        if (nextNode != null)
                        {
                            var newLeadingTrivia = nextNode.GetLeadingTrivia().InsertRange(0, leadingTrivia);
                            var newNextNode = nextNode.WithLeadingTrivia(newLeadingTrivia);
                            cleanedRoot = cleanedRoot.ReplaceNode(nextNode, newNextNode);
                        }
                        else
                        {
                            // No more usings, add to the first member or end of file
                            var firstMember = cleanedRoot.DescendantNodes(descendIntoTrivia: false)
                                .FirstOrDefault(n => n is MemberDeclarationSyntax);
                            if (firstMember != null)
                            {
                                var newLeadingTrivia = firstMember.GetLeadingTrivia().InsertRange(0, leadingTrivia);
                                var newFirstMember = firstMember.WithLeadingTrivia(newLeadingTrivia);
                                cleanedRoot = cleanedRoot.ReplaceNode(firstMember, newFirstMember);
                            }
                            else
                            {
                                // No members, add to the end of the file
                                var newTrailingTrivia = cleanedRoot.GetTrailingTrivia().AddRange(leadingTrivia);
                                cleanedRoot = cleanedRoot.WithTrailingTrivia(newTrailingTrivia);
                            }
                        }
                        

                    }
                    if (cleanedRoot is CompilationUnitSyntax cuNorm)
                    {
                        cleanedRoot = CollapseExtraEolAfterHeaderPreserveSpacing(cuNorm);
                    }
                    solution = solution.WithDocumentSyntaxRoot(documentId, cleanedRoot);
                }
            }

            return solution;
        }

        private static bool HasPreprocessorDirectives(SyntaxNode root)
        {
            foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
            {
                if (trivia.HasStructure && trivia.GetStructure() is DirectiveTriviaSyntax)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasRelevantTrivia(UsingDirectiveSyntax usingDirective)
        {
            

            var trailingTrivia = usingDirective.GetTrailingTrivia();
            var hasTrailingComment = trailingTrivia.Any(t =>
                !t.IsKind(SyntaxKind.WhitespaceTrivia) &&
                !t.IsKind(SyntaxKind.EndOfLineTrivia));
            return hasTrailingComment;
        }

        private static SyntaxTriviaList NormalizeHeaderTrivia(SyntaxTriviaList originalTrivia)
        {
            var processed = new List<SyntaxTrivia>();

            // Add all non-whitespace trivia
            foreach (var trivia in originalTrivia)
            {
                if (!trivia.IsKind(SyntaxKind.WhitespaceTrivia) &&
                    !trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    processed.Add(trivia);
                }
            }

            // Ensure exactly one newline at the end
            if (processed.Count > 0)
            {
                processed.Add(SyntaxFactory.ElasticCarriageReturnLineFeed);
            }

            return SyntaxFactory.TriviaList(processed);
        }

        private static CompilationUnitSyntax CollapseExtraEolAfterHeader(CompilationUnitSyntax cu)
        {
            var first = cu.GetFirstToken(includeZeroWidth: true);
            var leading = first.LeadingTrivia;

            // Find the last header comment at BOF (treat contiguous whitespace/EOL after comments as part of the header block)
            int lastComment = -1;
            for (int i = 0; i < leading.Count; i++)
            {
                var k = leading[i].Kind();
                if (k == SyntaxKind.SingleLineCommentTrivia ||
                    k == SyntaxKind.MultiLineCommentTrivia ||
                    k == SyntaxKind.SingleLineDocumentationCommentTrivia ||
                    k == SyntaxKind.MultiLineDocumentationCommentTrivia)
                {
                    lastComment = i;
                }
                else if (lastComment >= 0 && !(k == SyntaxKind.WhitespaceTrivia || k == SyntaxKind.EndOfLineTrivia))
                {
                    // stopped being header/spacing → done scanning
                    break;
                }
            }

            if (lastComment < 0)
                return cu; // no header at BOF

            // Scan the whitespace/EOL run immediately after the last comment
            int runStart = lastComment + 1;
            int runEnd = runStart;
            while (runEnd < leading.Count)
            {
                var k = leading[runEnd].Kind();
                if (k == SyntaxKind.WhitespaceTrivia || k == SyntaxKind.EndOfLineTrivia)
                    runEnd++;
                else
                    break;
            }

            // Count EOLs in that run
            int eolCount = 0;
            for (int i = runStart; i < runEnd; i++)
                if (leading[i].IsKind(SyntaxKind.EndOfLineTrivia))
                    eolCount++;

            // Already exactly one newline after header → nothing to do
            if (eolCount == 1)
                return cu;

            // Rebuild leading: keep everything through the last comment, then EXACTLY one newline
            var newLeading = new List<SyntaxTrivia>(runStart + 1);
            for (int i = 0; i <= lastComment; i++)
                newLeading.Add(leading[i]);

            newLeading.Add(SyntaxFactory.ElasticCarriageReturnLineFeed);

            var newFirst = first.WithLeadingTrivia(SyntaxFactory.TriviaList(newLeading));
            return cu.ReplaceToken(first, newFirst);
        }

        private static CompilationUnitSyntax CollapseExtraEolAfterHeaderPreserveSpacing(CompilationUnitSyntax cu)
        {
            var first = cu.GetFirstToken(includeZeroWidth: true);
            var leading = first.LeadingTrivia;

            // Find the last header comment
            int lastComment = -1;
            for (int i = 0; i < leading.Count; i++)
            {
                var k = leading[i].Kind();
                if (k == SyntaxKind.SingleLineCommentTrivia ||
                    k == SyntaxKind.MultiLineCommentTrivia ||
                    k == SyntaxKind.SingleLineDocumentationCommentTrivia ||
                    k == SyntaxKind.MultiLineDocumentationCommentTrivia)
                {
                    lastComment = i;
                }
                else if (lastComment >= 0 && !(k == SyntaxKind.WhitespaceTrivia || k == SyntaxKind.EndOfLineTrivia))
                {
                    break;
                }
            }

            if (lastComment < 0)
                return cu; // no header

            // Count EOLs after the last comment
            int runStart = lastComment + 1;
            int eolCount = 0;
            for (int i = runStart; i < leading.Count; i++)
            {
                if (leading[i].IsKind(SyntaxKind.EndOfLineTrivia))
                    eolCount++;
                else if (!leading[i].IsKind(SyntaxKind.WhitespaceTrivia))
                    break;
            }

            // Allow 1-2 newlines (preserve some spacing but not excessive)
            int targetEolCount = Math.Min(Math.Max(eolCount, 1), 2);
            
            if (eolCount == targetEolCount)
                return cu; // already correct

            // Rebuild with target number of newlines
            var newLeading = new List<SyntaxTrivia>(lastComment + 1 + targetEolCount);
            for (int i = 0; i <= lastComment; i++)
                newLeading.Add(leading[i]);

            for (int i = 0; i < targetEolCount; i++)
                newLeading.Add(SyntaxFactory.ElasticCarriageReturnLineFeed);

            var newFirst = first.WithLeadingTrivia(SyntaxFactory.TriviaList(newLeading));
            return cu.ReplaceToken(first, newFirst);
        }
    }
}
