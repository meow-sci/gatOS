namespace gatOS.NineP.Protocol;

/// <summary>
///     9P2000.L message type numbers (OS_PLAN.md T7.2; cross-checked against diod
///     <c>protocol.md</c> and OS_PLAN.md Appendix A). T-types the read-only server does not
///     implement get <c>Rlerror(EOPNOTSUPP)</c> from the dispatcher's default arm; unknown
///     byte values cast into this enum without being named, which is fine for that purpose.
/// </summary>
public enum MessageType : byte
{
    /// <summary>Error response carrying a Linux errno (the only error reply in 9P2000.L).</summary>
    Rlerror = 7,

    /// <summary>Filesystem statistics request.</summary>
    Tstatfs = 8,

    /// <summary>Filesystem statistics response.</summary>
    Rstatfs = 9,

    /// <summary>Open request (Linux open flags).</summary>
    Tlopen = 12,

    /// <summary>Open response.</summary>
    Rlopen = 13,

    /// <summary>Create request (unsupported here).</summary>
    Tlcreate = 14,

    /// <summary>Create response.</summary>
    Rlcreate = 15,

    /// <summary>Symlink request (unsupported here).</summary>
    Tsymlink = 16,

    /// <summary>Symlink response.</summary>
    Rsymlink = 17,

    /// <summary>Mknod request (unsupported here).</summary>
    Tmknod = 18,

    /// <summary>Mknod response.</summary>
    Rmknod = 19,

    /// <summary>Rename request (unsupported here).</summary>
    Trename = 20,

    /// <summary>Rename response.</summary>
    Rrename = 21,

    /// <summary>Readlink request (unsupported here).</summary>
    Treadlink = 22,

    /// <summary>Readlink response.</summary>
    Rreadlink = 23,

    /// <summary>Attribute request.</summary>
    Tgetattr = 24,

    /// <summary>Attribute response.</summary>
    Rgetattr = 25,

    /// <summary>Attribute-set request (unsupported here).</summary>
    Tsetattr = 26,

    /// <summary>Attribute-set response.</summary>
    Rsetattr = 27,

    /// <summary>Extended-attribute walk (the kernel probes this; clean EOPNOTSUPP expected).</summary>
    Txattrwalk = 30,

    /// <summary>Extended-attribute walk response.</summary>
    Rxattrwalk = 31,

    /// <summary>Extended-attribute create (unsupported here).</summary>
    Txattrcreate = 32,

    /// <summary>Extended-attribute create response.</summary>
    Rxattrcreate = 33,

    /// <summary>Directory read request.</summary>
    Treaddir = 40,

    /// <summary>Directory read response.</summary>
    Rreaddir = 41,

    /// <summary>Fsync request (unsupported here).</summary>
    Tfsync = 50,

    /// <summary>Fsync response.</summary>
    Rfsync = 51,

    /// <summary>POSIX lock request (unsupported here).</summary>
    Tlock = 52,

    /// <summary>POSIX lock response.</summary>
    Rlock = 53,

    /// <summary>POSIX getlock request (unsupported here).</summary>
    Tgetlock = 54,

    /// <summary>POSIX getlock response.</summary>
    Rgetlock = 55,

    /// <summary>Hard-link request (unsupported here).</summary>
    Tlink = 70,

    /// <summary>Hard-link response.</summary>
    Rlink = 71,

    /// <summary>Mkdir request (unsupported here).</summary>
    Tmkdir = 72,

    /// <summary>Mkdir response.</summary>
    Rmkdir = 73,

    /// <summary>Renameat request (unsupported here).</summary>
    Trenameat = 74,

    /// <summary>Renameat response.</summary>
    Rrenameat = 75,

    /// <summary>Unlinkat request (unsupported here).</summary>
    Tunlinkat = 76,

    /// <summary>Unlinkat response.</summary>
    Runlinkat = 77,

    /// <summary>Version/msize negotiation request.</summary>
    Tversion = 100,

    /// <summary>Version/msize negotiation response.</summary>
    Rversion = 101,

    /// <summary>Auth request (unsupported here — no auth on a loopback VM channel).</summary>
    Tauth = 102,

    /// <summary>Auth response.</summary>
    Rauth = 103,

    /// <summary>Attach request (binds a fid to the root).</summary>
    Tattach = 104,

    /// <summary>Attach response.</summary>
    Rattach = 105,

    /// <summary>Flush request (cancels an outstanding request — Ctrl-C on a blocked read).</summary>
    Tflush = 108,

    /// <summary>Flush response.</summary>
    Rflush = 109,

    /// <summary>Walk request (path traversal, up to 16 names).</summary>
    Twalk = 110,

    /// <summary>Walk response.</summary>
    Rwalk = 111,

    /// <summary>Read request.</summary>
    Tread = 116,

    /// <summary>Read response.</summary>
    Rread = 117,

    /// <summary>Write request (EACCES until control files land, M12).</summary>
    Twrite = 118,

    /// <summary>Write response.</summary>
    Rwrite = 119,

    /// <summary>Clunk request (releases a fid).</summary>
    Tclunk = 120,

    /// <summary>Clunk response.</summary>
    Rclunk = 121,

    /// <summary>Remove request (unsupported here).</summary>
    Tremove = 122,

    /// <summary>Remove response.</summary>
    Rremove = 123,
}
