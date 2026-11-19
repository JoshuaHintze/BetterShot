namespace BetterShot.Platform;

internal static class ShortcutClassifier
{
    internal const int S = 0x53;
    internal const int LShift = 0xA0;
    internal const int RShift = 0xA1;
    internal const int LWin = 0x5B;
    internal const int RWin = 0x5C;

    internal static bool IsExactWinShiftS(IReadOnlySet<int> keys)
    {
        if (!keys.Contains(S)) return false;
        if (!keys.Contains(LShift) && !keys.Contains(RShift)) return false;
        if (!keys.Contains(LWin) && !keys.Contains(RWin)) return false;
        return keys.All(k => k is S or LShift or RShift or LWin or RWin);
    }

    internal static bool ShouldSuppressKeyDown(int key, IReadOnlySet<int> pressed) =>
        key == S && IsExactWinShiftS(pressed);

    // A low-level hook can miss key-ups (hook timeouts while busy, secure desktop, elevated windows).
    // Drop remembered keys that Windows no longer reports as down so a stale Win cannot turn Shift+S into the shortcut.
    internal static void PruneReleasedKeys(HashSet<int> pressed, int currentKey, Func<int, bool> isDown) =>
        pressed.RemoveWhere(k => k != currentKey && !isDown(k));

    internal static bool ShouldSuppressKeyUp(int key, bool suppressedSIsDown) =>
        key == S && suppressedSIsDown;

    internal static bool RunSelfTests()
    {
        static HashSet<int> Keys(params int[] values) => [.. values];
        var cases = new (bool Expected, HashSet<int> Keys)[]
        {
            (true, Keys(LWin, LShift, S)),
            (true, Keys(RWin, RShift, S)),
            (true, Keys(LWin, RWin, LShift, RShift, S)),
            (false, Keys(LWin, LShift)),
            (false, Keys(LShift, S)),
            (false, Keys(LWin, S)),
            (false, Keys(LWin, LShift, S, 0x11)),
            (false, Keys(LWin, LShift, S, 0x41))
        };
        bool classification = cases.All(c => IsExactWinShiftS(c.Keys) == c.Expected);
        bool exactDownSuppressed = ShouldSuppressKeyDown(S, Keys(LWin, LShift, S));
        bool modifiedDownPasses = !ShouldSuppressKeyDown(S, Keys(LWin, LShift, S, 0x11));
        bool unrelatedDownPasses = !ShouldSuppressKeyDown(0x41, Keys(LWin, LShift, 0x41));
        bool pairedUpSuppressed = ShouldSuppressKeyUp(S, true);
        bool ordinaryUpPasses = !ShouldSuppressKeyUp(S, false) && !ShouldSuppressKeyUp(0x41, true);
        // A repeated S-down remains paired with one suppressed S-up, even if modifiers lift first.
        bool repeatSuppressed = ShouldSuppressKeyDown(S, Keys(LWin, LShift, S));
        bool keyUpAfterModifiersLifted = ShouldSuppressKeyUp(S, true);
        var stale = Keys(LWin, LShift, S);
        PruneReleasedKeys(stale, S, k => k is LShift);
        bool staleWinIgnored = !ShouldSuppressKeyDown(S, stale) && stale.SetEquals(Keys(LShift, S));
        var held = Keys(LWin, LShift, S);
        PruneReleasedKeys(held, S, k => k is LWin or LShift);
        bool heldChordKept = ShouldSuppressKeyDown(S, held);
        return classification && staleWinIgnored && heldChordKept && exactDownSuppressed && modifiedDownPasses && unrelatedDownPasses &&
               pairedUpSuppressed && ordinaryUpPasses && repeatSuppressed && keyUpAfterModifiersLifted;
    }
}
