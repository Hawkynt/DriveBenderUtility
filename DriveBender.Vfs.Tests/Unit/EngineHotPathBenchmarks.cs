using System.Diagnostics;
using DivisonM.Vfs.Caching;
using DivisonM.Vfs.Engine;
using FluentAssertions;
using NUnit.Framework;

namespace DivisonM.Vfs.Tests.Unit;

/// <summary>
/// The engine's hot paths priced WITHOUT a driver: a pool of real local members in temp folders,
/// driven straight through <see cref="PoolFileSystem"/>, the way <c>dbmount</c> builds it. The mount
/// matrix measures the product; this isolates what the ENGINE costs, so a number that moves here moved
/// because of engine code and not WinFsp, the kernel cache or the machine's other tenants.
///
/// Explicit: absolute rates are machine-dependent and the runs take seconds. Nothing here asserts a
/// timing — each prints its table and checks only that the work it timed was real.
/// </summary>
[TestFixture]
[Explicit("benchmark — run by name")]
[Category("Performance")]
public class EngineHotPathBenchmarks {

  private static readonly Guid _pool = Guid.Parse("b3c40000-0000-0000-0000-0000000000b3");

  private readonly List<string> _roots = [];

  [TearDown]
  public void TearDown() {
    foreach (var root in this._roots)
      try {
        Directory.Delete(root, true);
      } catch (IOException) {
        // best effort — a temp folder is not worth failing a benchmark over
      }

    this._roots.Clear();
  }

  private PoolFileSystem _NewPool(string configJson, string cacheSize = "512MiB", int members = 2) {
    var engineMembers = new EngineMember[members];
    for (var i = 0; i < members; ++i) {
      var root = Path.Combine(Path.GetTempPath(), "dbbench" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
      this._roots.Add(root);
      engineMembers[i] = new(new MeasuredVolumeIO(new LocalVolumeIO(Guid.NewGuid(), $"m{i}", root, $"PHYS-BENCH-{i}")));
    }

    var config = ConfigResolver.ResolveEffective(null, configJson);
    var fs = new PoolFileSystem(_pool, engineMembers, new CacheInstance("bench" + Guid.NewGuid().ToString("N"), new() { Size = cacheSize }), config);
    fs.Mount(new(@"X:\"));
    return fs;
  }

  private static void _WriteFile(PoolFileSystem fs, string path, long size) {
    var chunk = new byte[1 << 20];
    new Random(5).NextBytes(chunk);
    var handle = fs.Create(path, NodeKind.File, CreateFlags.None);
    for (long offset = 0; offset < size; offset += chunk.Length)
      fs.Write(handle, chunk.AsSpan(0, (int)Math.Min(chunk.Length, size - offset)), offset, WriteMode.Normal);

    fs.Close(handle);
  }

  private static void _ReadAll(PoolFileSystem fs, string path, long size) {
    var buffer = new byte[1 << 20];
    var handle = fs.Open(path, AccessMode.Read, ShareMode.Read);
    for (long offset = 0; offset < size;)
      offset += fs.Read(handle, buffer, offset);

    fs.Close(handle);
  }

  /// <summary>Runs <paramref name="threads"/> workers for <paramref name="duration"/>, each on its own pre-opened handle; returns ops/s.</summary>
  private static double _Rate(int threads, TimeSpan duration, Func<int, NodeHandle> open, Action<NodeHandle, Random> op, Action<NodeHandle> close) {
    var handles = Enumerable.Range(0, threads).Select(open).ToArray();
    var counts = new long[threads * 16]; // padded: a shared cache line would be measured as contention
    using var start = new ManualResetEventSlim();
    var stop = false;
    var workers = Enumerable.Range(0, threads).Select(worker => new Thread(() => {
      var random = new Random(9871 + worker);
      var handle = handles[worker];
      start.Wait();
      long done = 0;
      while (!Volatile.Read(ref stop)) {
        op(handle, random);
        ++done;
      }

      counts[worker * 16] = done;
    }) { IsBackground = true }).ToArray();

    foreach (var worker in workers)
      worker.Start();

    var clock = Stopwatch.StartNew();
    start.Set();
    Thread.Sleep(duration);
    Volatile.Write(ref stop, true);
    foreach (var worker in workers)
      worker.Join();

    clock.Stop();
    foreach (var handle in handles)
      close(handle);

    return counts.Sum() / clock.Elapsed.TotalSeconds;
  }

  private static readonly int[] _threadCounts = [1, 2, 4, 8, Environment.ProcessorCount];

  [Test]
  [Description("Cached random 4 KiB reads, one handle per thread, all on ONE file and each on its OWN file.")]
  public void CachedRandomRead_ScalingAcrossThreads() {
    const long size = 128L << 20;
    using var fs = this._NewPool("{}");
    var files = Enumerable.Range(0, Environment.ProcessorCount).Select(i => $"r{i}.bin").ToArray();
    _WriteFile(fs, files[0], size);
    _ReadAll(fs, files[0], size); // warm: every later read is a page-cache hit
    foreach (var file in files.Skip(1)) {
      _WriteFile(fs, file, 8L << 20);
      _ReadAll(fs, file, 8L << 20);
    }

    TestContext.Out.WriteLine("threads | same file IOPS | own file IOPS");
    foreach (var threads in _threadCounts) {
      var same = _Rate(threads, TimeSpan.FromSeconds(2),
        _ => fs.Open(files[0], AccessMode.Read, ShareMode.Read),
        (handle, random) => _RandomRead(fs, handle, random, size), fs.Close);
      var own = _Rate(threads, TimeSpan.FromSeconds(2),
        worker => fs.Open(files[worker], AccessMode.Read, ShareMode.Read),
        (handle, random) => _RandomRead(fs, handle, random, 8L << 20), fs.Close);
      TestContext.Out.WriteLine($"{threads,7} | {same,14:N0} | {own,13:N0}");
      same.Should().BeGreaterThan(0);
    }
  }

  [ThreadStatic]
  private static byte[]? _readBuffer;

  private static void _RandomRead(PoolFileSystem fs, NodeHandle handle, Random random, long size) {
    var buffer = _readBuffer ??= new byte[4096];
    if (fs.Read(handle, buffer, random.NextInt64(size / 4096) * 4096) != 4096)
      throw new InvalidOperationException("short read"); // not an assertion library: it would be timed too
  }

}
