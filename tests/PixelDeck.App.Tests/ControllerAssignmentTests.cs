using PixelDeck.App.Input;

namespace PixelDeck.App.Tests;

/// <summary>
/// Resolving configured controller slots against the devices actually present.
/// </summary>
/// <remarks>
/// The case that motivated this: a profile assigning player one to slot 2 while
/// the only controller sits in slot 1. The game ignored the controller entirely,
/// and the only way to play was to open the settings page and reassign it by
/// hand.
/// </remarks>
public sealed class ControllerAssignmentTests
{
    private static readonly int[] ClaimedSlots = [1, 3];

    private static GamepadConnections Connected(params int[] slots)
    {
        var mask = 0;
        foreach (var slot in slots)
        {
            mask |= 1 << slot;
        }

        return new GamepadConnections(mask);
    }

    private static int[] Resolve(int[] configured, GamepadConnections connections)
    {
        var assignments = configured.ToArray();
        ControllerAssignment.Resolve(assignments, connections);
        return assignments;
    }

    [Fact]
    public void PlayerOneGetsTheOnlyControllerEvenWhenConfiguredForAnother()
    {
        // The reported bug, exactly: configured for slot 1, plugged into slot 0.
        var resolved = Resolve([1, 0, 2, 3], Connected(0));

        Assert.Equal(0, resolved[0]);
    }

    [Fact]
    public void APreferenceThatIsPluggedInIsKept()
    {
        // Someone who deliberately chose the second pad keeps it.
        var resolved = Resolve([1, 0, 2, 3], Connected(0, 1));

        Assert.Equal(1, resolved[0]);
        Assert.Equal(0, resolved[1]);
    }

    [Fact]
    public void TwoPlayersOnOneConnectedPadDoNotBothClaimIt()
    {
        var resolved = Resolve([2, 3, 0, 1], Connected(0));

        Assert.Equal(0, resolved[0]);
        // Nothing is left for player two, so its preference stands rather than
        // being silently pointed at player one's device.
        Assert.NotEqual(0, resolved[1]);
    }

    [Fact]
    public void FourPlayersSpreadAcrossFourPadsInOrder()
    {
        var resolved = Resolve([0, 1, 2, 3], Connected(0, 1, 2, 3));

        Assert.Equal([0, 1, 2, 3], resolved);
    }

    [Fact]
    public void PlayersTakeSparseSlotsInPlayerOrder()
    {
        // Every player configured for slot 0, but the pads are in 1 and 3.
        var resolved = Resolve([0, 0, 0, 0], Connected(1, 3));

        Assert.Equal(1, resolved[0]);
        Assert.Equal(3, resolved[1]);
        // Only two devices exist. Players three and four land on empty slots, and
        // crucially not on slot 1 or 3, which would have them driving a pad that
        // already belongs to someone else.
        Assert.DoesNotContain(resolved[2], ClaimedSlots);
        Assert.DoesNotContain(resolved[3], ClaimedSlots);
        Assert.NotEqual(resolved[2], resolved[3]);
    }

    [Fact]
    public void PlayerOneTakesAConnectedPadAheadOfALaterPlayersPreference()
    {
        // Player two's saved preference names the only connected pad. Player one
        // still gets it: a single controller has to drive player one, which is the
        // whole point. Honouring the preference first would recreate the fault.
        var resolved = Resolve([0, 1, 2, 3], Connected(1));

        Assert.Equal(1, resolved[0]);
        Assert.NotEqual(1, resolved[1]);
    }

    [Fact]
    public void NoTwoPlayersEverShareADevice()
    {
        var resolved = Resolve([0, 0, 0, 0], Connected(0, 1, 2, 3));

        Assert.Equal(4, resolved.Distinct().Count());
    }

    [Fact]
    public void NoControllersLeavesPreferencesUntouched()
    {
        // Rewriting them here would lose the player's choice for no benefit,
        // and the settings page would stop reflecting what they picked.
        var resolved = Resolve([2, 3, 0, 1], Connected());

        Assert.Equal([2, 3, 0, 1], resolved);
    }
}
