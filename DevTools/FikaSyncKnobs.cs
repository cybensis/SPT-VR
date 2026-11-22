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
                description = "How much of the way a VR player's chest turns to where their head looks. Tarkov's rifle stance stands the body about 35° side-on; 1 = the chest squares up to the head (it takes its share after the hips have turned, rows below)." },
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
