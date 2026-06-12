namespace gatOS.NineP.Vfs;

/// <summary>
///     A directory whose listing and lookup are supplied as delegates (OS_PLAN.md T7.1) —
///     covers both fixed directories (closure over an array) and dynamic ones whose children
///     are rebuilt from live data on every call (M8's <c>by-id</c>, <c>active</c>).
/// </summary>
public sealed class DelegateDirectory : VfsDirectory
{
    private readonly Func<IReadOnlyList<VfsNode>> _list;
    private readonly Func<string, VfsNode?>? _lookup;

    /// <param name="name">The entry name.</param>
    /// <param name="qidPath">The stable qid path number.</param>
    /// <param name="list">Produces the children in a stable order; must be thread-safe.</param>
    /// <param name="lookup">
    ///     Optional name resolver; when omitted, lookup scans <paramref name="list"/>. Supply
    ///     one when children can be built without enumerating everything.
    /// </param>
    public DelegateDirectory(
        string name,
        ulong qidPath,
        Func<IReadOnlyList<VfsNode>> list,
        Func<string, VfsNode?>? lookup = null)
        : base(name, qidPath)
    {
        _list = list;
        _lookup = lookup;
    }

    /// <summary>Creates a directory over a fixed set of children.</summary>
    public static DelegateDirectory Fixed(string name, ulong qidPath, params VfsNode[] children)
        => new(name, qidPath, () => children);

    /// <inheritdoc />
    public override IReadOnlyList<VfsNode> List() => _list();

    /// <inheritdoc />
    public override VfsNode? Lookup(string name)
    {
        if (_lookup is not null)
            return _lookup(name);
        foreach (var child in _list())
            if (child.Name == name)
                return child;
        return null;
    }
}
