// Not part of the mod. check_mod_runtime.py compiles the shell with this stub only to get a reference DLL for the
// module check: the game's runtime has no System.IO.File/FileStream/Directory (pb-001), so a shell with a known use
// (KNOWN in check_mod_runtime.py; none since the old file bridge was deleted) fails that compile, and without a DLL
// the module was checked against the last make-mod's, which lacks any shell API added since. The real shell compile
// (without the stub) still reports everything else. Add a member here when a known use needs one.
namespace System.IO
{
    public enum FileMode { CreateNew = 1, Create = 2, Open = 3, OpenOrCreate = 4, Truncate = 5, Append = 6 }

    public enum FileAccess { Read = 1, Write = 2, ReadWrite = 3 }

    [System.Flags]
    public enum FileShare { None = 0, Read = 1, Write = 2, ReadWrite = 3, Delete = 4 }

    public static class File
    {
        public static bool Exists(string path) { return false; }
    }

    public static class Directory
    {
        public static bool Exists(string path) { return false; }
        public static object CreateDirectory(string path) { return null; }
    }

    public class FileStream : System.IDisposable
    {
        public FileStream(string path, FileMode mode, FileAccess access, FileShare share) { }
        public long Position { get { return 0L; } set { } }
        public long Length { get { return 0L; } }
        public int Read(byte[] buffer, int offset, int count) { return 0; }
        public void Write(byte[] buffer, int offset, int count) { }
        public void Flush() { }
        public void Close() { }
        public void Dispose() { }
    }
}
