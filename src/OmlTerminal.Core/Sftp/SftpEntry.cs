namespace OmlTerminal.Core.Sftp;

public sealed record SftpEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTime Modified, bool IsSymbolicLink);
