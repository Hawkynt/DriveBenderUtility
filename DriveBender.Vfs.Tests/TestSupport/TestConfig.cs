namespace DivisonM.Vfs.Tests.TestSupport;

/// <summary>
/// The configuration the engine's unit suite runs under: the built-in defaults with a DURABLE close,
/// and reads that are not checked against block indexes.
///
/// The product closes the POSIX way — a close returns at once and a background publisher makes the
/// file durable within about a second (write.closeIsDurable off). Most of this suite pins down what
/// the write path leaves on each member, and it reads the members the moment a close returns; with
/// a close that publishes later it would be timing the publisher instead. So it runs with the
/// publish inside the close, and the POSIX close has suites of its own (PosixCloseTests and the
/// end-to-end tests, which mount with the product defaults).
///
/// Likewise block-checked reads: they add an index lookup to every block read and a background
/// indexer reading every file, which the suite's I/O counts and timings were not written for.
/// BlockIntegrityTests covers them, and a test that sets integrity.verifyReads itself still gets
/// what it asked for.
/// </summary>
internal static class TestConfig {

  public const string BaselineLayer = """{ "write": { "closeIsDurable": true }, "integrity": { "verifyReads": "never", "blockIndexMinSize": "1EiB" } }""";

  public static PoolConfig Resolve(string? poolJson) => ConfigResolver.ResolveEffective(BaselineLayer, poolJson);

}
