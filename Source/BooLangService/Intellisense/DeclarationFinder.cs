using System.Collections.Generic;
using System.Text.RegularExpressions;
using Boo.BooLangService;
using Boo.BooLangService.Document;
using Boo.BooLangService.Document.Nodes;
using Boo.BooLangService.Intellisense;
using Boo.BooLangService.VSInterop;
using Boo.Lang.Compiler.TypeSystem;
using Microsoft.VisualStudio.Package;

namespace Boo.BooLangService.Intellisense
{
    public class DeclarationFinder
    {
        private const string ImportKeyword = "import";
        private readonly List<string> excludedMembers = new List<string> {".ctor", "constructor"};

        private readonly CompiledProject compiledProject;
        private readonly Regex IntellisenseTargetRegex = new Regex("[^ (]*$", RegexOptions.Compiled);
        private readonly ILineView lineView;
        private readonly string fileName;
        private readonly IProjectReferenceLookup projectReferences;

        public DeclarationFinder(CompiledProject compiledProject, IProjectReferenceLookup projectReferenceLookup, ILineView lineView, string fileName)
        {
            this.compiledProject = compiledProject;
            this.projectReferences = projectReferenceLookup;
            this.lineView = lineView;
            this.fileName = fileName;
        }

        /// <summary>
        /// Finds any intellisense declarations relative to the current caret position.
        /// </summary>
        /// <param name="lineNum">Caret line</param>
        /// <param name="colNum">Caret column</param>
        /// <param name="reason">Reason for parse</param>
        /// <returns>IntellisenseDeclarations</returns>
        public IntellisenseDeclarations Find(int lineNum, int colNum, ParseReason reason)
        {
            string line = lineView.GetTextUptoPosition(lineNum, colNum);

            if (line.StartsWith(ImportKeyword))
            {
                // handle this separately from normal intellisense, because:
                //  a) the open import statement will have broken the document
                //  b) we don't need the doc anyway, all imports would be external to the current file
                // only problem is, the top level namespaces (i.e. System, Boo, Microsoft) should be
                // usable from within code too, so we need some way of parsing and caching them and
                // making them available everywhere
                return GetImportIntellisenseDeclarations(line);
            }

            if (line.EndsWith(".") || reason == ParseReason.MemberSelect)
            {
                // Member Select: it's easier to check if the line ends in a . rather than checking
                // the parse reason, because the parse reason may not technically be correct.
                // for example "Syst[ctrl+space]" is a complete word, while "System[.]" is
                // a member lookup; however, "System.[ctrl+space]" is a member lookup but it gets
                // reported as a complete word because of the shortcut used.
                // So it's easier just to say any lines ending in "." are member lookups, and everything
                // else is complete word.
                return GetMemberLookupIntellisenseDeclarations(lineNum, colNum);
            }

            // Everything else (complete word)
            return GetScopedIntellisenseDeclarations(lineNum);
        }

        private IntellisenseDeclarations GetImportIntellisenseDeclarations(string line)
        {
            // get any namespace already written (i.e. "Boo.Lang.")
            string intellisenseTarget = GetIntellisenseTarget(line);
            var declarations = new IntellisenseDeclarations();

            AddNamespacesFromReferences(declarations, intellisenseTarget);

            return declarations;
        }

        private IntellisenseDeclarations GetMemberLookupIntellisenseDeclarations(int line, int column)
        {
            var declarations = new IntellisenseDeclarations();

            IEntity entity = compiledProject.GetEntityAt(fileName, line, column);
            var namespaceEntity = entity as INamespace;
            var instance = false;

            if (namespaceEntity == null && entity is InternalLocal)
            {
                namespaceEntity = ((InternalLocal)entity).Type;
                instance = true;
            }

            var members = new List<IEntity>(TypeSystemServices.GetAllMembers(namespaceEntity));

            // remove any static members for instances, and any instance members for types
            members.RemoveAll(e =>
            {
                if (excludedMembers.Contains(e.Name)) return true;
                if (e is INamespace) return false;

                var member = (IMember)e;

                if (!member.IsPublic) return true;
                return (instance && member.IsStatic) || (!instance && !member.IsStatic);
            });

            // optimise this so the above is removed, and there's only one loop
            // this method creates the delcarations from the members brought back by the TypeSystemServices
            // I'm not too happy with this, as it is big and high maintenance.

            var converter = new EntityToTreeNodeConverter();

            members.ForEach(e =>
            {
                IBooParseTreeNode node = converter.ToTreeNode(e);

                if (node != null)
                    declarations.Add(node);
            });

            declarations.Sort();

            return declarations;
        }

        private IntellisenseDeclarations GetScopedIntellisenseDeclarations(int lineNum)
        {
            // get the node that the caret is in
            var scopedParseTree = compiledProject.GetScope(fileName, lineNum);
            var declarations = new IntellisenseDeclarations();

            AddMembersFromScopeTree(declarations, scopedParseTree);
            AddKeywords(declarations, scopedParseTree);
            AddImports(declarations, GetDocument(scopedParseTree));
            AddReferences(declarations);

            declarations.Sort();

            return declarations;
        }

        private DocumentTreeNode GetDocument(IBooParseTreeNode node)
        {
            var currentNode = node;

            while (!(currentNode is DocumentTreeNode))
            {
                currentNode = currentNode.Parent;
            }

            return currentNode as DocumentTreeNode;
        }

        private string GetIntellisenseTarget(string line)
        {
            return IntellisenseTargetRegex.Match(line).Groups[0].Value;
        }

        /// <summary>
        /// Adds members from the current scope (flattened, so all containing scopes are included) to
        /// the declarations.
        /// </summary>
        private void AddMembersFromScopeTree(IntellisenseDeclarations declarations, IBooParseTreeNode scopedParseTree)
        {
            var parseTreeFlattener = new BooParseTreeNodeFlatterner();

            declarations.Add(parseTreeFlattener.FlattenFrom(scopedParseTree));
        }

        /// <summary>
        /// Adds keywords based on the current scope to the declarations.
        /// </summary>
        private void AddKeywords(IntellisenseDeclarations declarations, IBooParseTreeNode scopedParseTree)
        {
            var keywords = new TypeKeywordResolver();

            declarations.Add(keywords.GetForScope(scopedParseTree));
        }

        /// <summary>
        /// Adds any types and namespaces, imported at the start of the document, to the declarations.
        /// </summary>
        private void AddImports(IntellisenseDeclarations declarations, DocumentTreeNode document)
        {
            // add imports to declarations
            foreach (var importNamespace in document.Imports.Keys)
            {
                var importedNodes = document.Imports[importNamespace];

                declarations.Add(importedNodes);
            }
        }

        private void AddReferences(IntellisenseDeclarations declarations)
        {
            AddNamespacesFromReferences(declarations, "");
        }

        /// <summary>
        /// Adds namespaces from references to the declarations.
        /// </summary>
        private void AddNamespacesFromReferences(IntellisenseDeclarations declarations, string namespaceContinuation)
        {
            declarations.Add(projectReferences.GetReferencedNamespacesInProjectContaining(fileName, namespaceContinuation));
        }
    }
}