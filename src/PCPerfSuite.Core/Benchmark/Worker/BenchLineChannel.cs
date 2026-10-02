using System.Text;
using PCPerfSuite.Core.Benchmark.Protocol;

namespace PCPerfSuite.Core.Benchmark.Worker;

/// <summary>
/// Un flux duplex vu comme des lignes de messages : écriture sous verrou (plusieurs threads envoient), lecture par un
/// seul thread. Une écriture qui échoue (tube fermé) marque le canal cassé ; rien ne lève vers l'appelant.
/// </summary>
public sealed class BenchLineChannel : IDisposable
{
    private readonly Stream _stream;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly object _writeGate = new();
    private volatile bool _broken;

    public BenchLineChannel(Stream stream)
    {
        _stream = stream;
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _writer = new StreamWriter(stream, encoding, bufferSize: 1 << 16, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        _reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 16, leaveOpen: true);
    }

    public bool IsBroken => _broken;

    public bool TrySend(BenchMessage message)
    {
        if (_broken) return false;
        string line = BenchMessageCodec.Encode(message);
        lock (_writeGate)
        {
            try
            {
                _writer.WriteLine(line);
                return true;
            }
            catch (Exception)
            {
                _broken = true;
                return false;
            }
        }
    }

    /// <summary>Null à la fin du flux ou sur un tube cassé.</summary>
    public string? ReadLine()
    {
        try
        {
            return _reader.ReadLine();
        }
        catch (Exception)
        {
            _broken = true;
            return null;
        }
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancel)
    {
        try
        {
            return await _reader.ReadLineAsync(cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broken = true;
            return null;
        }
    }

    public void Close()
    {
        _broken = true;
        try { _stream.Dispose(); } catch (Exception) { /* déjà fermé */ }
    }

    public void Dispose() => Close();
}
