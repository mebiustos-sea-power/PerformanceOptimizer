using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PerformanceOptimizer.Optimizations;
using PerformanceOptimizer.Simulation;
using static NativeSimulationFixture;
using static Program;

internal static class SimulationTests
{
    internal static void Run()
    {
        WeaponLookup(); FormationLookup(); Channels(); RejectChangedLayouts();
        ScratchTests.Run();
    }

    private static void WeaponLookup()
    {
        var fixture = new NativeSimulationFixture();
        var native = fixture.Compile<Func<Owner, string, bool, List<Weapon>?>>(WeaponLookupAllocation.Target);
        var changed = fixture.Compile<Func<Owner, string, bool, List<Weapon>?>>(WeaponLookupAllocation.Target,
            (code, _) => WeaponLookupAllocation.Transpile(code));
        var unit = new Owner(); var source = new List<Weapon> { new(1), new(2), new(1) };
        unit._ammunitionToWeaponSystemsDictionary.Add("ammo", source);
        foreach (bool copy in new[] { false, true })
        {
            var a = native(unit, "ammo", copy)!; var b = changed(unit, "ammo", copy)!;
            Require(a.SequenceEqual(b) && ReferenceEquals(a, source) == !copy && ReferenceEquals(b, source) == !copy,
                "Weapon lookup changed item order, duplicates or copy/alias semantics.");
            if (copy) { b.Clear(); Require(source.Count == 3, "A mutable result changed the unit's registered weapons."); }
            Require(native(unit, "missing", copy) == null && changed(unit, "missing", copy) == null, "Missing ammo behavior changed.");
            Throws<ArgumentNullException>(() => native(unit, null!, copy));
            Throws<ArgumentNullException>(() => changed(unit, null!, copy));
        }
        unit._ammunitionToWeaponSystemsDictionary = null!;
        Throws<NullReferenceException>(() => native(unit, "ammo", false));
        Throws<NullReferenceException>(() => changed(unit, "ammo", false));
        Console.WriteLine("PASS native weapon lookup IL: order, duplicates, mutable copies, missing entries and exceptions match");
    }

    private static void FormationLookup()
    {
        var fixture = new NativeSimulationFixture();
        var native = fixture.Compile<Func<Owner, bool>>(FormationStationLookup.Following);
        var changed = fixture.Compile<Func<Owner, bool>>(FormationStationLookup.Following, FormationStationLookup.Transpile);
        var unit = new Owner();
        foreach (bool assigned in new[] { false, true, true, false, true })
        foreach (bool independent in new[] { false, true })
        {
            var formation = new Formation { Result = assigned ? new Station() : null };
            if (formation.Result != null) formation.Result.UnitActsIndependently.Data = independent;
            unit.Formation = formation;
            bool expected = native(unit); int oldQueries = formation.Queries; formation.Queries = 0;
            Require(changed(unit) == expected && formation.Queries == 1 && oldQueries == (assigned ? 2 : 1),
                "Formation state changed or a duplicate lookup remained.");
        }
        unit.Formation = null;
        Require(!native(unit) && !changed(unit), "Leaving a formation retained a cached station.");
        Console.WriteLine("PASS native formation getter IL: assignment/removal/independent changes match; assigned lookups 2 -> 1");
    }

    private static Owner Sensors()
    {
        var unit = new Owner();
        var other = new Owner();
        var ecm = new EcmSensor { _systemType = 9 }; ecm._ecm._ep._type = 1; ecm.IsOn.Data = true;
        var laser = new LaserSensor { _systemType = 11, _targetChannels = 3 }; laser.IsOn.Data = true; laser._associatedTargets.Add(other);
        unit._obp._sensorSystems.Add(new Sensor { _systemType = 2 });
        unit._obp._sensorSystems.Add(ecm); unit._obp._sensorSystems.Add(laser);
        return unit;
    }

    private static void Channels()
    {
        var fixture = new NativeSimulationFixture();
        foreach (var target in new[] { SensorChannelEnumeration.Jam, SensorChannelEnumeration.Laser })
        {
            var native = fixture.Compile<Action<Owner>>(target);
            var changed = fixture.Compile<Action<Owner>>(target, SensorChannelEnumeration.Transpile);
            for (int mode = 0; mode < 8; mode++)
            {
                var a = Sensors(); var b = Sensors();
                foreach (var unit in new[] { a, b })
                {
                    var ecm = (EcmSensor)unit._obp._sensorSystems[1]; var laser = (LaserSensor)unit._obp._sensorSystems[2];
                    ecm.IsOn.Data = (mode & 1) == 0; laser.IsOn.Data = (mode & 2) == 0;
                    ecm._geoPositionTargeted = (mode & 4) != 0;
                    if (mode == 7) unit._obp._sensorSystems.Clear();
                }
                native(a); changed(b);
                Require(a.NumberOfFreeJamChannels.Writes.SequenceEqual(b.NumberOfFreeJamChannels.Writes) &&
                    a.NumberOfFreeLaserChannels.Writes.SequenceEqual(b.NumberOfFreeLaserChannels.Writes), "Sensor channel totals/notifications changed.");
            }
            foreach (bool optimize in new[] { false, true })
            {
                var unit = Sensors();
                var operation = optimize ? changed : native;
                if (target == SensorChannelEnumeration.Jam) ((EcmSensor)unit._obp._sensorSystems[1])._ecm._ep._type = 0;
                unit._obp._sensorSystems[1].IsOn.OnRead = () => unit._obp._sensorSystems[0] = new Sensor();
                if (target == SensorChannelEnumeration.Laser)
                    unit._obp._sensorSystems[2].IsOn.OnRead = () => unit._obp._sensorSystems[0] = new Sensor();
                Throws<InvalidOperationException>(() => operation(unit));
                Require(unit.NumberOfFreeJamChannels.Writes.Count == 0 && unit.NumberOfFreeLaserChannels.Writes.Count == 0,
                    "A changed sensor list published a partial channel total.");
            }
        }
        Console.WriteLine("PASS native sensor channel IL: on/off/occupied/empty totals and notifications match; same-count mutations still fail");
    }

    private static void RejectChangedLayouts()
    {
        var changed = InstructionReader.Read(WeaponLookupAllocation.Target); changed[0].opcode = OpCodes.Ldnull; changed[0].operand = null;
        Throws<InvalidOperationException>(() => WeaponLookupAllocation.Transpile(changed).ToList());
        var ai = InstructionReader.Read(AiContactScratchReuse.Target);
        ai.RemoveAll(i => i.opcode == OpCodes.Newobj && i.operand is ConstructorInfo c && c.DeclaringType!.IsGenericType &&
            c.DeclaringType.GetGenericTypeDefinition() == typeof(HashSet<>));
        Throws<InvalidOperationException>(() => AiContactScratchReuse.Transpile(ai).ToList());
        // Also validate the real private sonar snapshot type and complete method layouts.
        SonarScratchReuse.TranspileSchedule(InstructionReader.Read(SonarScratchReuse.Passive), SonarScratchReuse.Passive).ToList();
        SonarScratchReuse.TranspileSchedule(InstructionReader.Read(SonarScratchReuse.Active), SonarScratchReuse.Active).ToList();
        SonarScratchReuse.TranspileCommit(InstructionReader.Read(SonarScratchReuse.Commit)).ToList();
        AiContactScratchReuse.Transpile(InstructionReader.Read(AiContactScratchReuse.Target)).ToList();
        Console.WriteLine("PASS simulation contracts: native sonar/AI layouts accepted; incompatible allocation sites rejected");
    }

    internal static void InstallOnMono()
    {
        // Native simulation methods can initialize Unity-dependent globals while
        // Mono prepares a detour. That requires a Unity player, so this host tests
        // the real transpilers through managed-boundary IL and real hook fixtures.
        ScratchTests.InstallOnMono();
        Console.WriteLine("PASS game Mono: production scratch hooks; native simulation detour installation requires live Unity verification");
    }
}
