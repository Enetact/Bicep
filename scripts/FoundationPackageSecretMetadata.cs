using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

public sealed record PackageLiteralRange(string File, int HeapOffset, int StartLine, int EndLine, int Utf8Bytes);
public sealed record PackageLiteralExtraction(bool HasMetadata, int HeapBytes, int Entries, long Utf8Bytes, string[] Files, PackageLiteralRange[] Ranges);
public sealed record PackageSecretProcessResult(int ExitCode, string Stdout, string Stderr);

// Reads PE metadata as data. Never loads, resolves or invokes an assembly from the inspected package.
public static class FoundationPackageSecretMetadata
{
    public static int CountLines(byte[] bytes) { int lines = 1; foreach (var value in bytes) if (value == 10) lines++; return lines; }
    static async Task<string> ReadBounded(StreamReader reader, CancellationTokenSource deadline)
    {
        var output = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), deadline.Token)) > 0)
        {
            if (output.Length + count > 1048576) { deadline.Cancel(); throw new IOException("PackageSecretProcessOutputLimit"); }
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }
    // Starts only an explicitly pinned trusted local tool, never a package entry. No inherited credential/config environment.
    public static PackageSecretProcessResult Run(string executable, string[] arguments, string directory, int timeoutMilliseconds)
    {
        if (timeoutMilliseconds < 50 || timeoutMilliseconds > 150000) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        using var process = new Process();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
        start.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR" }) { var value = Environment.GetEnvironmentVariable(name); if (value != null) start.Environment[name] = value; }
        foreach (var name in new[] { "TEMP", "TMP", "HOME", "USERPROFILE" }) start.Environment[name] = directory;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        process.StartInfo = start; using var deadline = new CancellationTokenSource(timeoutMilliseconds); bool started = false;
        try
        {
            if (!(started = process.Start())) throw new IOException("PackageSecretProcessStartFailed");
            var stdout = ReadBounded(process.StandardOutput, deadline); var stderr = ReadBounded(process.StandardError, deadline);
            Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token)).GetAwaiter().GetResult();
            return new(process.ExitCode, stdout.Result, stderr.Result);
        }
        finally
        {
            if (started && !process.HasExited) { process.Kill(false); if (!process.WaitForExit(5000)) throw new IOException("PackageSecretProcessDidNotStop"); }
        }
    }
    public static PackageLiteralExtraction Extract(string path, string directory, string stem, long remainingBytes)
    {
        const int heapLimit = 16777216, stringLimit = 1048576, chunkLimit = 4194304;
        if (remainingBytes < 0 || remainingBytes > 134217728) throw new InvalidDataException("PackageSecretProjectionLimit");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 67108864) throw new InvalidDataException("PackageSecretBinaryLimit");
        using var pe = new PEReader(input, PEStreamOptions.LeaveOpen | PEStreamOptions.PrefetchMetadata);
        if (!pe.HasMetadata) return new(false, 0, 0, 0, Array.Empty<string>(), Array.Empty<PackageLiteralRange>());
        var reader = pe.GetMetadataReader(); var size = reader.GetHeapSize(HeapIndex.UserString);
        if (size > heapLimit) throw new InvalidDataException("PackageSecretMetadataHeapLimit");
        var encoding = new UTF8Encoding(false, true); var files = new List<string>(); var ranges = new List<PackageLiteralRange>();
        StreamWriter writer = null; string outputName = null; int line = 1, previous = 0, entries = 0; long total = 0;
        try
        {
            var handle = size > 1 ? MetadataTokens.UserStringHandle(1) : default;
            while (!handle.IsNil)
            {
                var offset = MetadataTokens.GetHeapOffset(handle);
                if (offset <= previous || offset >= size || ++entries > 100000) throw new InvalidDataException("PackageSecretMetadataTraversalLimit");
                previous = offset; var value = reader.GetUserString(handle);
                // A NUL in one unrelated literal must not make an entire projection look like a binary file.
                var text = new StringBuilder(value.Length);
                foreach (var character in value) text.Append((character < 32 && character != '\n' && character != '\r' && character != '\t') || character == 127 ? '\n' : character);
                var normalized = text.ToString().Replace("\r\n", "\n").Replace('\r', '\n'); var bytes = encoding.GetByteCount(normalized);
                if (bytes > stringLimit || total + bytes + 1 > remainingBytes) throw new InvalidDataException("PackageSecretProjectionLimit");
                if (writer == null || writer.BaseStream.Position + bytes + 1 > chunkLimit)
                {
                    writer?.Dispose(); outputName = stem + "-" + files.Count.ToString("D6") + ".txt";
                    var output = new FileStream(Path.Combine(directory, outputName), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    writer = new StreamWriter(output, encoding) { NewLine = "\n", AutoFlush = true }; files.Add(outputName); line = 1;
                }
                var first = line; foreach (var character in normalized) if (character == '\n') line++;
                writer.Write(normalized); writer.Write('\n'); ranges.Add(new(outputName, offset, first, line, bytes)); line++;
                total += bytes + 1; handle = reader.GetNextHandle(handle);
            }
            return new(true, size, entries, total, files.ToArray(), ranges.ToArray());
        }
        finally { writer?.Dispose(); }
    }
}
