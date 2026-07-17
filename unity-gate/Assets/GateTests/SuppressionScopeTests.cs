using System;
using System.IO;
using NUnit.Framework;
using SceneBuilder.Editor;

// Gate for SuppressionScope — the echo-suppression primitive placed at the two write seams
// (code->scene scene write, source/sidecar write) so the auto-sync loop never re-triggers on its
// own writes. This suite exercises the PRIMITIVE + the write registry directly; the async
// event-drop consumers (ObjectChangeEvents handler, FileSystemWatcher handler) are proven
// end-to-end in b7-t2 (no-ping-pong).
public class SuppressionScopeTests
{
    private string? _tempDir;

    [SetUp]
    public void SetUp()
    {
        SuppressionScope.ResetForTests();
    }

    [TearDown]
    public void TearDown()
    {
        SuppressionScope.ResetForTests();
        if (_tempDir != null && Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
        _tempDir = null;
    }

    private string TempFilePath(string name)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sb_suppress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        return Path.Combine(_tempDir, name);
    }

    [Test]
    public void SceneWriteSuppressed_FalseByDefault()
    {
        Assert.IsFalse(SuppressionScope.SceneWriteSuppressed,
            "With no outstanding scope, the flag must be false.");
    }

    [Test]
    public void SuppressScene_SetsFlag_ClearsOnDispose()
    {
        using (SuppressionScope.SuppressScene())
        {
            Assert.IsTrue(SuppressionScope.SceneWriteSuppressed,
                "Flag must be true while a scope is open.");
        }

        Assert.IsFalse(SuppressionScope.SceneWriteSuppressed,
            "Flag must drop once the (only) outstanding scope is disposed.");
    }

    [Test]
    public void SuppressScene_RefCountNests_StaysSuppressedUntilLastDispose()
    {
        var outer = SuppressionScope.SuppressScene();
        var inner = SuppressionScope.SuppressScene();

        Assert.IsTrue(SuppressionScope.SceneWriteSuppressed, "Suppressed with two outstanding scopes.");

        inner.Dispose();
        Assert.IsTrue(SuppressionScope.SceneWriteSuppressed,
            "Disposing ONE of two nested scopes must NOT clear the flag — the outer scope is still open.");

        outer.Dispose();
        Assert.IsFalse(SuppressionScope.SceneWriteSuppressed,
            "Disposing the LAST outstanding scope must clear the flag.");
    }

    [Test]
    public void SuppressScene_FlagDrops_WhenGuardedBodyThrows()
    {
        try
        {
            using (SuppressionScope.SuppressScene())
            {
                throw new InvalidOperationException("simulated failure inside the guarded body");
            }
        }
        catch (InvalidOperationException)
        {
            // expected — the point is that Dispose still ran on unwind.
        }

        Assert.IsFalse(SuppressionScope.SceneWriteSuppressed,
            "The scope must drain its ref-count via Dispose even when the guarded body throws.");
    }

    [Test]
    public void SuppressScene_TimeBound_ExpiresLeakedScope()
    {
        // Open with an already-elapsed bound and deliberately never dispose (simulating a leak).
        // The time-bound backstop must stop suppression anyway, with no real sleep needed.
        SuppressionScope.SuppressScene(boundSeconds: -1);

        Assert.IsFalse(SuppressionScope.SceneWriteSuppressed,
            "A scope opened with an already-elapsed time-bound must not suppress.");
    }

    [Test]
    public void ComputeContentHash_IsDeterministic_AndDistinguishesContent()
    {
        var h1 = SuppressionScope.ComputeContentHash("hello world");
        var h2 = SuppressionScope.ComputeContentHash("hello world");
        var h3 = SuppressionScope.ComputeContentHash("hello world!");

        Assert.AreEqual(h1, h2, "Same content must hash identically.");
        Assert.AreNotEqual(h1, h3, "Different content must not collide.");
    }

    [Test]
    public void WriteIfChanged_RecordsOwnWrite_InRegistry()
    {
        var path = TempFilePath("Demo.cs");
        const string contents = "// demo builder source";

        var wrote = SceneBuilderPaths.WriteIfChanged(path, contents);

        Assert.IsTrue(wrote, "First write to a nonexistent file must actually write.");
        var hash = SuppressionScope.ComputeContentHash(contents);
        Assert.IsTrue(SuppressionScope.IsOwnWrite(path, hash),
            "WriteIfChanged must record (path, hash) so IsOwnWrite recognizes our own write.");
        Assert.IsFalse(SuppressionScope.IsOwnWrite(path, SuppressionScope.ComputeContentHash("different content")),
            "A different content hash for the same path must NOT be reported as our own write.");
    }

    [Test]
    public void WriteIfChanged_NoOpWrite_DoesNotRecord()
    {
        var path = TempFilePath("Demo.cs");
        const string contents = "// demo builder source";
        File.WriteAllText(path, contents);
        var hash = SuppressionScope.ComputeContentHash(contents);

        var wrote = SceneBuilderPaths.WriteIfChanged(path, contents);

        Assert.IsFalse(wrote, "Byte-identical content already on disk must be a no-op.");
        Assert.IsFalse(SuppressionScope.IsOwnWrite(path, hash),
            "A no-op WriteIfChanged (nothing actually written) must NOT populate the registry.");
    }
}
