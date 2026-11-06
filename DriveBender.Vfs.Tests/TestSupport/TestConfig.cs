namespace DivisonM.Vfs.Tests.TestSupport;

/// <summary>
/// The configuration the engine's unit suite runs under: the built-in defaults with a DURABLE close.
///
/// The product closes the POSIX way — a close returns at once and a background publisher makes the
/// file durable within about a second (write.closeIsDurable off). Most of this suite pins down what
/// the write path leaves on each member, and it reads the members the moment a close returns; with
/// a close that publishes later it would be timing the publisher instead. So it runs with the
/// publish inside the close, and the POSIX close has suites of its own (PosixCloseTests and the
/// end-to-end tests, which mount with the product defaults).
/// </summary>
internal static class TestConfig {

  public const string DurableCloseLayer = """{ "write": { "closeIsDurable": true } }""";

  public static PoolConfig Resolve(string? poolJson) => ConfigResolver.ResolveEffective(DurableCloseLayer, poolJson);

}
