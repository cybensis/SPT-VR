using TarkovVR.Source.Dev;

namespace SptVrDevTools
{
    // The FikaSync module's knobs: that module ships to every co-op player and doesn't reference SPT-VR.dll, so it carries no
    // [Tunable] markers. Listed here instead (Tools/debug_panel/gen_debug_panel.py reads this file for the panel too); bound when
    // the module is loaded.
    internal static class FikaSyncKnobs
    {
        internal sealed class Knob
        {
            public string assembly = "SPT-VR-FikaSync";
            public string type;
            public string field;
            public string section;
            public TunableKind kind;
            public string description;
            public string unit;
        }

        internal static readonly Knob[] All =
        {
            new Knob { type = "TarkovVR.Source.Player.Body.UpperBodySolver", field = "twistShare", section = "Body", kind = TunableKind.Tune, unit = null,
                description = "With Square Chest on: how much of a VR player's glance (their head off the way their body faces) the chest turns too. Off: how much of the way the chest turns from Tarkov's side-on stance to where their head looks." },
            new Knob { type = "TarkovVR.Source.Player.Body.UpperBodySolver", field = "squareChest", section = "Body", kind = TunableKind.Switch, unit = null,
                description = "A VR player's chest faces the way their body faces (Tarkov's rifle stance turns it about 35° side-on), then turns part of a glance. Off = it only turns part of the way from that stance to their head." },
            new Knob { type = "SptVrFikaSync.HeadSyncApply", field = "turnBodyWithLook", section = "Body", kind = TunableKind.Switch, unit = null,
                description = "A VR player's body turns with their look, as on their own screen. Off = Tarkov's own rule: standing still the body stays up to 45° off their look, and their head, hands and gun are drawn turned with it." },
            new Knob { type = "SptVrFikaSync.ArmSyncApply", field = "weaponForwardOffset", section = "Body", kind = TunableKind.Tune, unit = "m",
                description = "Pushes a VR player's gun (and free off hand) this far forward along their body's facing. 0 = where they hold it." },
            new Knob { type = "TarkovVR.Source.Player.Body.UpperBodySolver", field = "maxTwist", section = "Body", kind = TunableKind.Tune, unit = "°",
                description = "The most the spine twists the chest round, in all." },
            new Knob { type = "TarkovVR.Source.Player.Body.UpperBodySolver", field = "spineMaxTurn", section = "Body", kind = TunableKind.Tune, unit = "°",
                description = "The most each of the 3 spine bones may turn off the animated pose (twist and lean together)." },
            new Knob { type = "TarkovVR.Source.Player.Body.UpperBodySolver", field = "neckLookShare", section = "Body", kind = TunableKind.Tune, unit = null,
                description = "How much of the head's turn off the chest the neck takes (the rest is the head's)." },
            new Knob { type = "TarkovVR.Source.Player.Body.UpperBodySolver", field = "hipShare", section = "Body", kind = TunableKind.Tune, unit = null,
                description = "Standing still, a VR player's hips (and legs) are squared to the way their body faces, which follows their head. This is how much of a glance off that the hips turn too; the chest takes the rest. 0 = the hips stay square to the body." },
            new Knob { type = "TarkovVR.Source.Player.Body.UpperBodySolver", field = "maxHipTurn", section = "Body", kind = TunableKind.Tune, unit = "°",
                description = "The most the hips turn off the animated pose." },
            new Knob { type = "SptVrFikaSync.HeadSyncApply", field = "turnHips", section = "Body", kind = TunableKind.Switch, unit = null,
                description = "The other VR players' hips turn toward their head at all (standing still; not prone, sprinting or on a mounted gun)." },
            new Knob { type = "SptVrFikaSync.HeadSyncApply", field = "hipStillSpeed", section = "Body", kind = TunableKind.Tune, unit = "m/s",
                description = "Up to this speed the hips turn fully." },
            new Knob { type = "SptVrFikaSync.HeadSyncApply", field = "hipMovingSpeed", section = "Body", kind = TunableKind.Tune, unit = "m/s",
                description = "From this speed the hips are back to the walk's own (the legs go the way you walk)." },
            new Knob { type = "SptVrFikaSync.HeadSyncApply", field = "hipBlendTime", section = "Body", kind = TunableKind.Tune, unit = "s",
                description = "How long the hips take to turn in or back." },
            new Knob { type = "SptVrFikaSync.DoorSyncApply", field = "smoothRate", section = "Doors in co-op", kind = TunableKind.Tune, unit = "/s",
                description = "How fast the door you see eases toward their newest angle (the arms use the same). Higher = closer behind, steppier; 0 = it jumps to each packet." },
            new Knob { type = "SptVrFikaSync.DoorSyncApply", field = "shutSoundMaxWait", section = "Doors in co-op", kind = TunableKind.Tune, unit = "s",
                description = "The shut sound plays when the door you see reaches its frame, or after this long at most." },
            new Knob { type = "SptVrFikaSync.DoorSyncApply", field = "slamSpeed", section = "Doors in co-op", kind = TunableKind.Tune, unit = "°/s",
                description = "A door shut this fast plays its shut sound at full volume (slower = quieter, down to Tarkov's own volume)." },
            new Knob { type = "SptVrFikaSync.DoorSyncApply", field = "drivenHoldTime", section = "Doors in co-op", kind = TunableKind.Tune, unit = "s",
                description = "How long after their last door packet Tarkov's own swing of that door stays skipped (their hand moves it)." },
            new Knob { type = "SptVrFikaSync.BreachReplay", field = "kickWhenDropped", section = "Doors in co-op", kind = TunableKind.Switch, unit = null,
                description = "When FIKA's replay of their gun breach doesn't kick the door open on your side, kick it here." },
            new Knob { type = "SptVrFikaSync.BreachReplay", field = "logBreaches", section = "Doors in co-op", kind = TunableKind.Log, unit = null,
                description = "A [DoorSync] line per breach they make: kicked by FIKA's replay, or here, and their copy's movement state." },
        };
    }
}
