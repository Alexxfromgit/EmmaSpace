using EmmaSpace.Core.Model;

namespace EmmaSpace.Core.Analysis;

internal static class TreeWalk
{
    /// <summary>All live nodes in the subtree of <paramref name="scope"/>, including it.</summary>
    public static IEnumerable<int> Subtree(FileTree tree, int scope)
    {
        if (scope == FileTree.Root)
        {
            // Fast path: a linear pass over the arrays.
            for (var i = 0; i < tree.Count; i++)
                if (!tree.Nodes[i].IsDeleted) yield return i;
            yield break;
        }

        var stack = new Stack<int>();
        stack.Push(scope);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var c in tree.Children(node)) stack.Push(c);
        }
    }

    public static IEnumerable<int> Files(FileTree tree, int scope) =>
        Subtree(tree, scope).Where(n => !tree.Nodes[n].IsDirectory);
}
