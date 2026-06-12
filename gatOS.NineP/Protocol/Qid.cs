using gatOS.NineP.Vfs;

namespace gatOS.NineP.Protocol;

/// <summary>
///     A 9p qid: the server's unique identity for a file (wire form
///     <c>type[1] version[4] path[8]</c>). The guest kernel keys inodes on it.
/// </summary>
/// <param name="Type">The high type bits (<see cref="QidType"/>).</param>
/// <param name="Version">Cache-validity version; this server always sends 0.</param>
/// <param name="Path">The tree-unique node id (<see cref="VfsNode.QidPath"/>).</param>
public readonly record struct Qid(QidType Type, uint Version, ulong Path)
{
    /// <summary>Size of the wire form in bytes.</summary>
    public const int WireSize = 13;

    /// <summary>The qid for a VFS node.</summary>
    public static Qid ForNode(VfsNode node)
        => new(node.IsDirectory ? QidType.Directory : QidType.File, 0, node.QidPath);
}

/// <summary>The qid type byte (only the values a read-only fs serves).</summary>
public enum QidType : byte
{
    /// <summary>A plain file (QTFILE).</summary>
    File = 0x00,

    /// <summary>A directory (QTDIR).</summary>
    Directory = 0x80,
}
