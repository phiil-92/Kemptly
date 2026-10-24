using System.Security.Cryptography;

namespace Kemptly.Core;

public class DupFile : ObservableObject
{
    public required string FullPath { get; init; }
    public required long Size { get; init; }
    public required DateTime Modified { get; init; }
    public string Name => Path.GetFileName(FullPath);
    public string Folder => Path.GetDirectoryName(FullPath) ?? "";

    bool _delete;
    public bool Delete { get => _delete; set => Set(ref _delete, value); }
}

public class DupGroup
{
    public required List<DupFile> Files { get; init; }
    public long Size => Files[0].Size;
    public long Wasted => Size * (Files.Count - 1);
    public string Title => $"{Files[0].Name}  ·  {Files.Count} copies  ·  {Fmt.Bytes(Size)} each";
    public string WastedText => $"{Fmt.Bytes(Wasted)} wasted";
}

/// <summary>Finds files with identical content: same size, then same first 64 KB, then same full SHA-256.</summary>
public static class DuplicateFinder
{
    public static List<DupGroup> Find(string root, long minSize, IProgress<string> progress, CancellationToken ct)
    {
        var bySize = new Dictionary<long, List<FileInfo>>();
        long count = 0;
        foreach (var f in FileWalker.Walk(root, ct: ct))
        {
            if ((f.Attributes & FileAttributes.System) != 0) continue;
            long len;
            try { len = f.Length; } catch { continue; }
            if (len < minSize) continue;
            if (!bySize.TryGetValue(len, out var l)) bySize[len] = l = [];
            l.Add(f);
            if (++count % 500 == 0) progress.Report($"Scanning… {count:N0} files");
        }

        var candidates = bySize.Values.Where(l => l.Count > 1).ToList();
        var groups = new List<DupGroup>();
        int done = 0;
        foreach (var sameSize in candidates)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report($"Comparing contents… {++done:N0} of {candidates.Count:N0} size groups");

            // Hard links are one file on disk under several names: deleting one frees nothing.
            var unique = sameSize.GroupBy(f => Native.GetFileId(f.FullName) is { } id ? $"{id.Volume}:{id.Index}" : f.FullName)
                                 .Select(g => g.First()).ToList();
            if (unique.Count < 2) continue;

            foreach (var partial in unique.GroupBy(f => Hash(f.FullName, 64 * 1024)).Where(g => g.Key != null && g.Count() > 1))
                foreach (var full in partial.GroupBy(f => Hash(f.FullName, long.MaxValue, ct)).Where(g => g.Key != null && g.Count() > 1))
                {
                    var files = full.Select(f => new DupFile { FullPath = f.FullName, Size = f.Length, Modified = f.LastWriteTime })
                                    .OrderBy(f => f.FullPath.Length).ThenBy(f => f.Modified).ToList();
                    for (int i = 1; i < files.Count; i++) files[i].Delete = true; // keep the copy with the shortest path
                    groups.Add(new DupGroup { Files = files });
                }
        }
        return groups.OrderByDescending(g => g.Wasted).ToList();
    }

    static string? Hash(string path, long maxBytes, CancellationToken ct = default)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[1 << 20];
            long left = maxBytes;
            int n;
            while (left > 0 && (n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left))) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha.AppendData(buf, 0, n);
                left -= n;
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
