using System;
using System.Collections;
using System.Collections.Generic;
using Boo.BooLangService.Document.Nodes;
using Boo.BooLangService.Intellisense;
using Boo.Lang.Compiler.TypeSystem;
using Microsoft.VisualStudio.Package;

namespace Boo.BooLangService
{
    /// <summary>
    /// Contains a list of declarations to be contained within the intellisense list.
    /// </summary>
    public class IntellisenseDeclarations : Declarations
    {
        private readonly IntellisenseIconResolver icons = new IntellisenseIconResolver();
        private readonly BooParseTreeNodeList members = new BooParseTreeNodeList();

        public override int GetCount()
        {
            return members.Count;
        }

        public override string GetDescription(int index)
        {
            IBooParseTreeNode node = members[index];

            return node.GetIntellisenseDescription();
        }

        public override string GetDisplayText(int index)
        {
            return members[index].Name;
        }

        public override int GetGlyph(int index)
        {
            IBooParseTreeNode node = members[index];
            
            return (int)icons.Resolve(node);
        }

        public override string GetName(int index)
        {
            return GetDisplayText(index);
        }

        public virtual void AddRange(IList<IBooParseTreeNode> list)
        {
            members.AddRange(list);
        }

        public void Add(string[] keywords)
        {
            // still a bit hacky
            foreach (var keyword in keywords)
            {
                Add(new KeywordTreeNode(keyword));
            }
        }

        public virtual void Add(IBooParseTreeNode member)
        {
            members.Add(member);
        }

        public void Sort()
        {
            members.Sort();
        }

        public IBooParseTreeNode Find(Predicate<IBooParseTreeNode> match)
        {
            return members.Find(match);
        }

        public void RemoveAll(Predicate<IBooParseTreeNode> match)
        {
            for (var i = members.Count - 1; i <= 0; i++)
            {
                if (match(members[i]))
                    members.RemoveAt(i);
            }
        }
    }
}