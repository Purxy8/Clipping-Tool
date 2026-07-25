using ClipForge.Services;

namespace ClipForge.Tests;

internal static class TrimFreeSpacePolicyTests
{
    internal static Task RunAsync()
    {
        const string localPath = @"C:\ClipForge\Clips";
        const string localQueryPath = @"\\?\C:\ClipForge\Clips\";
        AssertSpacePolicy(
            "local sufficient",
            localPath,
            localQueryPath,
            ulong.MaxValue,
            querySucceeded: true,
            expected: true);
        AssertSpacePolicy(
            "local insufficient",
            localPath,
            localQueryPath,
            availableFreeBytes: 0,
            querySucceeded: true,
            expected: false);

        const string uncPath = @"\\server\share\ClipForge\Clips";
        const string uncQueryPath = @"\\?\UNC\server\share\ClipForge\Clips\";
        AssertSpacePolicy(
            "UNC sufficient",
            uncPath,
            uncQueryPath,
            ulong.MaxValue,
            querySucceeded: true,
            expected: true);
        AssertSpacePolicy(
            "UNC insufficient",
            uncPath,
            uncQueryPath,
            availableFreeBytes: 0,
            querySucceeded: true,
            expected: false);
        AssertSpacePolicy(
            "UNC query failure",
            uncPath,
            uncQueryPath,
            ulong.MaxValue,
            querySucceeded: false,
            expected: false);

        var longPath = @"C:\" + string.Join(
            '\\',
            Enumerable.Repeat(new string('a', 48), 6));
        Require(longPath.Length > 260, "The long-path regression input was not long enough.");
        AssertSpacePolicy(
            "extended-length local sufficient",
            longPath,
            @"\\?\" + longPath + '\\',
            ulong.MaxValue,
            querySucceeded: true,
            expected: true);

        return Task.CompletedTask;
    }

    private static void AssertSpacePolicy(
        string scenario,
        string rootDirectory,
        string expectedQueryPath,
        ulong availableFreeBytes,
        bool querySucceeded,
        bool expected)
    {
        string? actualQueryPath = null;
        var queryCount = 0;
        var provider = new WindowsClipTrimFreeSpaceProvider(
            (
                string directoryPath,
                out ulong freeBytesAvailableToCaller,
                out ulong totalNumberOfBytes,
                out ulong totalNumberOfFreeBytes) =>
            {
                queryCount++;
                actualQueryPath = directoryPath;
                freeBytesAvailableToCaller = availableFreeBytes;
                totalNumberOfBytes = availableFreeBytes;
                totalNumberOfFreeBytes = availableFreeBytes;
                return querySucceeded;
            });

        var actual = ClipTrimService.HasSufficientWorkingSpace(
            rootDirectory,
            sourceFileSizeBytes: 100L * 1024 * 1024,
            sourceDuration: TimeSpan.FromSeconds(10),
            width: 1920,
            height: 1080,
            framesPerSecond: 60,
            trimDuration: TimeSpan.FromSeconds(5),
            provider);

        Require(actual == expected, $"{scenario}: free-space decision was {actual}; expected {expected}.");
        Require(queryCount == 1, $"{scenario}: expected exactly one free-space query; saw {queryCount}.");
        Require(
            string.Equals(actualQueryPath, expectedQueryPath, StringComparison.Ordinal),
            $"{scenario}: queried '{actualQueryPath}' instead of '{expectedQueryPath}'.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
