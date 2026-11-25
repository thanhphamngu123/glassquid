using Microsoft.Win32;
using System.Diagnostics;
using System.IO;

namespace Glassquid.Dock;

internal enum NightLightQuickActionState
{
    Unavailable,
    Off,
    On,
    Searching,
}

/// <summary>Reads and changes the same Night light state used by the Windows taskbar.</summary>
internal sealed class NightLightQuickActionController : IDisposable
{
    private bool _disposed;
    private bool _togglePending;

    public event Action<NightLightQuickActionState>? StateChanged;

    public NightLightQuickActionState State { get; private set; } = NightLightQuickActionState.Searching;

    public string? Error { get; private set; }

    public async Task RefreshAsync()
    {
        if (_disposed || _togglePending) return;

        try
        {
            var enabled = await Task.Run(NightLight.IsEnabled);
            Error = enabled is null ? "Windows reports Night light is unavailable on this device." : null;
            SetState(enabled is null
                ? NightLightQuickActionState.Unavailable
                : enabled.Value ? NightLightQuickActionState.On : NightLightQuickActionState.Off);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Debug.WriteLine($"[Glassquid Night light] Could not read Windows state: {ex}");
            SetState(NightLightQuickActionState.Unavailable);
        }
    }

    public async Task<bool> ToggleAsync()
    {
        if (_disposed || _togglePending) return false;
        _togglePending = true;
        Error = null;
        SetState(NightLightQuickActionState.Searching);

        try
        {
            var isActive = await Task.Run(NightLight.Toggle);
            if (isActive is null)
                throw new InvalidOperationException("Windows reports Night light is unavailable on this device.");
            SetState(isActive.Value ? NightLightQuickActionState.On : NightLightQuickActionState.Off);
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Debug.WriteLine($"[Glassquid Night light] Could not change Windows state: {ex}");
            try
            {
                var current = await Task.Run(NightLight.IsEnabled);
                SetState(current is null
                    ? NightLightQuickActionState.Unavailable
                    : current.Value ? NightLightQuickActionState.On : NightLightQuickActionState.Off);
            }
            catch
            {
                SetState(NightLightQuickActionState.Unavailable);
            }
            return false;
        }
        finally
        {
            _togglePending = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StateChanged = null;
    }

    private void SetState(NightLightQuickActionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }
}

/// <summary>Reads and updates Night light's native strength setting.</summary>
internal sealed class NightLightStrengthController
{
    private int _writePending;

    public event Action<int>? StrengthChanged;

    public int Strength { get; private set; } = -1;

    public string? Error { get; private set; }

    public async Task RefreshAsync()
    {
        if (Volatile.Read(ref _writePending) != 0) return;

        try
        {
            var strength = await Task.Run(NightLightCloudStore.ReadStrength);
            if (Volatile.Read(ref _writePending) != 0) return;
            Error = null;
            if (Strength == strength) return;
            SetStrength(strength);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Debug.WriteLine($"[Glassquid Night light] Could not read Windows strength: {ex}");
        }
    }

    public async Task<bool> SetStrengthAsync(int strength)
    {
        if (Interlocked.Exchange(ref _writePending, 1) != 0) return false;

        try
        {
            var requested = Math.Clamp(strength, 0, 100);
            await Task.Run(() => NightLightCloudStore.WriteStrength(requested));
            var applied = await Task.Run(NightLightCloudStore.ReadStrength);
            if (applied != requested)
                throw new InvalidOperationException("Windows replaced the Night light strength before it could be applied.");
            Error = null;
            SetStrength(applied);
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Debug.WriteLine($"[Glassquid Night light] Could not change Windows strength: {ex}");
            return false;
        }
        finally
        {
            Volatile.Write(ref _writePending, 0);
        }
    }

    private void SetStrength(int strength)
    {
        if (Strength == strength) return;
        Strength = strength;
        StrengthChanged?.Invoke(strength);
    }
}

/// <summary>
/// Shared CloudStore serialization and Night light strength access.
/// Microsoft does not document this binary format, so support can vary by Windows build.
/// </summary>
internal static class NightLightCloudStore
{
    private const string SettingsStoreRoot =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount";
    private const string SettingsCloudRoot =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Cloud";
    internal const string StateValueName = "Data";
    internal const byte BoolType = 2;
    internal const byte UInt64Type = 6;
    private const byte StructType = 10;
    private const byte ListType = 11;
    private const byte Int8Type = 14;
    private const byte Int16Type = 15;
    internal const byte Int32Type = 16;
    private const byte StopType = 0;
    private const byte StopBaseType = 1;

    internal readonly record struct Field(ushort Id, byte Type, byte[] Value);
    internal readonly record struct StateEnvelope(ulong Timestamp, byte[] Payload);
    private readonly record struct SettingsRecord(
        string RegistryKeyPath,
        string CloudRecordKeyPath,
        ulong Timestamp,
        List<Field> Fields,
        bool HasBaseStop,
        bool IsPerDevice,
        int BranchPriority);
    private static readonly object SettingsWriteSync = new();
    private static ulong _highestSettingsTimestamp;
    private static bool _settingsStartupMarginApplied;

    public static int ReadStrength()
    {
        var settings = ReadSettingsRecords();
        if (settings.Count == 0)
            throw new InvalidOperationException("Night light strength is not available in this Windows profile.");

        var selected = settings
            .Select(record => (Record: record, Temperature: record.Fields.FirstOrDefault(field => field.Id == 40)))
            .Where(candidate => candidate.Temperature.Id == 40)
            .OrderByDescending(candidate => candidate.Record.Timestamp)
            .ThenBy(candidate => candidate.Record.BranchPriority)
            .ThenByDescending(candidate => candidate.Record.IsPerDevice)
            .FirstOrDefault();

        if (selected.Temperature.Id != 40)
            return 0; // Windows defaults an omitted temperature to 6500 K.
        if (selected.Temperature.Type != Int16Type)
            throw new InvalidDataException("Windows has no readable Night light color temperature.");

        var kelvin = DecodeZigZag(DecodeVarUInt64(selected.Temperature.Value));
        if (kelvin is < 1200 or > 6500)
            throw new InvalidDataException("Windows returned a Night light color temperature outside the supported range.");
        return Math.Clamp((int)Math.Round((6500 - kelvin) / 53.0), 0, 100);
    }

    public static void WriteStrength(int strength)
    {
        strength = Math.Clamp(strength, 0, 100);
        var kelvin = 6500 - (int)Math.Round(strength * 53.0);

        lock (SettingsWriteSync)
        {
            var settings = ReadSettingsRecords();
            if (settings.Count == 0)
                throw new InvalidOperationException("Windows Night light settings are not available in this profile.");

            var nightLightOn = NightLight.IsEnabled() == true || NightLightDisplayEffect.IsActive;
            if (nightLightOn)
            {
                NightLightDisplayEffect.Apply(strength);
            }

            var timestamp = NextSettingsTimestamp(settings);
            foreach (var record in settings)
                WriteSettings(record, kelvin, nightLightOn, timestamp);

            if (nightLightOn)
            {
                // Windows uses the preview flag to apply a temperature change immediately.
                Thread.Sleep(50);
                settings = ReadSettingsRecords();
                if (settings.Count == 0)
                    throw new InvalidOperationException("Windows Night light settings disappeared during the update.");
                timestamp = NextSettingsTimestamp(settings);
                foreach (var record in settings)
                    WriteSettings(record, null, previewing: false, timestamp);
            }
        }
    }

    private static List<SettingsRecord> ReadSettingsRecords()
    {
        var records = new List<SettingsRecord>();
        using var root = Registry.CurrentUser.OpenSubKey(SettingsStoreRoot, writable: false);
        if (root is null) return records;

        foreach (var branchName in root.GetSubKeyNames())
        {
            var branchPriority = branchName.Equals("Current", StringComparison.OrdinalIgnoreCase) ? 0
                : branchName.Equals("Cache", StringComparison.OrdinalIgnoreCase) ? 1
                : branchName.Equals("Cloud", StringComparison.OrdinalIgnoreCase) ? 2
                : 3;
            using var branch = root.OpenSubKey(branchName, writable: false);
            if (branch is null) continue;

            foreach (var containerName in branch.GetSubKeyNames().Where(name =>
                         name.Contains("bluelightreduction.settings", StringComparison.OrdinalIgnoreCase)))
            {
                var containerPath = JoinRegistryPath(SettingsStoreRoot, branchName, containerName);
                ReadSettingsRecordsBelow(containerPath, branchName, containerName, branchPriority, records);
            }
        }

        return records
            .GroupBy(record => record.RegistryKeyPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(record => record.BranchPriority).First())
            .ToList();
    }

    private static void ReadSettingsRecordsBelow(
        string registryPath,
        string branchName,
        string containerName,
        int branchPriority,
        List<SettingsRecord> records)
    {
        using var key = Registry.CurrentUser.OpenSubKey(registryPath, writable: false);
        if (key is null) return;

        if (key.GetValue(StateValueName) is byte[] data)
        {
            try
            {
                var envelope = ReadStateEnvelope(data);
                var hasBaseStop = envelope.Payload.Length >= 2
                    && envelope.Payload[^2] == StopBaseType
                    && envelope.Payload[^1] == StopType;
                var fields = ReadMarshaledStruct(envelope.Payload, allowTerminalBaseStop: hasBaseStop);
                if (fields.Any(field => field.Id is 20 or 30 or 40 or 50 or 60 or 70))
                {
                    var branchPrefix = JoinRegistryPath(SettingsStoreRoot, branchName) + "\\";
                    var cloudPath = registryPath.StartsWith(branchPrefix, StringComparison.OrdinalIgnoreCase)
                        ? JoinRegistryPath(SettingsCloudRoot, registryPath[branchPrefix.Length..])
                        : registryPath;
                    records.Add(new SettingsRecord(
                        registryPath,
                        cloudPath,
                        envelope.Timestamp,
                        fields,
                        hasBaseStop,
                        containerName.Contains("settingsperdevice", StringComparison.OrdinalIgnoreCase),
                        branchPriority));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException or ArgumentException)
            {
                // CloudStore Cloud records contain timestamps rather than a settings envelope;
                // only full settings payloads are valid candidates for reading or writing.
                Debug.WriteLine($"[Glassquid Night light] Ignoring non-settings CloudStore record {registryPath}: {ex.Message}");
            }
        }

        foreach (var childName in key.GetSubKeyNames())
            ReadSettingsRecordsBelow(
                JoinRegistryPath(registryPath, childName), branchName, containerName, branchPriority, records);
    }

    private static void WriteSettings(SettingsRecord current, int? kelvin, bool previewing, ulong timestamp)
    {
        var fields = current.Fields
            .Where(field => field.Id != 70 && (kelvin is null || field.Id != 40))
            .ToList();
        if (kelvin is { } value)
            fields.Add(new Field(40, Int16Type, EncodeVarUInt64(EncodeZigZag(value))));
        if (previewing)
            fields.Add(new Field(70, BoolType, [1]));

        var payload = WriteMarshaledStruct(fields.OrderBy(field => field.Id), current.HasBaseStop);
        var envelope = WriteStateEnvelope(timestamp, payload);
        using var key = Registry.CurrentUser.OpenSubKey(current.RegistryKeyPath, writable: true)
            ?? throw new InvalidOperationException("Glassquid could not update Windows Night light strength.");
        key.SetValue(StateValueName, envelope, RegistryValueKind.Binary);
        key.Flush();
    }

    private static ulong NextSettingsTimestamp(IReadOnlyCollection<SettingsRecord> records)
    {
        var baseline = _highestSettingsTimestamp;
        foreach (var record in records)
        {
            baseline = Math.Max(baseline, record.Timestamp);
            try
            {
                baseline = Math.Max(baseline, ReadCloudRecordTimestamp(record.CloudRecordKeyPath));
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException or ArgumentException)
            {
                Debug.WriteLine($"[Glassquid Night light] Could not read a CloudStore timestamp: {ex.Message}");
            }
        }

        var step = _settingsStartupMarginApplied ? 2UL : 699UL;
        var timestamp = Math.Max(
            (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            AddTimestamp(baseline, step));
        _highestSettingsTimestamp = timestamp;
        _settingsStartupMarginApplied = true;
        return timestamp;
    }

    private static ulong AddTimestamp(ulong timestamp, ulong amount) =>
        timestamp > ulong.MaxValue - amount ? ulong.MaxValue : timestamp + amount;

    internal static ulong ReadCloudRecordTimestamp(string cloudRecordKeyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(cloudRecordKeyPath, writable: false);
        if (key?.GetValue(StateValueName) is not byte[] data) return 0;
        var fields = ReadMarshaledStruct(data);
        var timestamp = fields.FirstOrDefault(field => field.Id == 1);
        return timestamp.Id == 1 && timestamp.Type == UInt64Type
            ? DecodeVarUInt64(timestamp.Value)
            : 0;
    }

    private static ulong EncodeZigZag(int value) => unchecked((ulong)((value << 1) ^ (value >> 31)));

    private static int DecodeZigZag(ulong value) => unchecked((int)((value >> 1) ^ (ulong)-(long)(value & 1)));

    private static byte[] ReadInt8List(byte[] value)
    {
        var offset = 0;
        if (ReadByte(value, ref offset) != Int8Type)
            throw new InvalidDataException("Windows returned an unsupported Night light payload list.");
        var length = ReadVarUInt64(value, ref offset);
        if (length != (ulong)(value.Length - offset))
            throw new InvalidDataException("Windows returned a truncated Night light payload.");
        return value.AsSpan(offset).ToArray();
    }

    internal static List<Field> ReadMarshaledStruct(byte[] data, bool allowTerminalBaseStop = false)
    {
        if (data.Length < 4 || data[0] != 0x43 || data[1] != 0x42 || data[2] != 0x01 || data[3] != 0x00)
            throw new InvalidDataException("Windows returned an unsupported Night light data format.");
        return ReadStruct(data.AsSpan(4).ToArray(), allowTerminalBaseStop);
    }

    private static List<Field> ReadStruct(byte[] data, bool allowTerminalBaseStop = false)
    {
        var fields = new List<Field>();
        var offset = 0;
        ushort previousId = 0;
        while (offset < data.Length)
        {
            var header = ReadByte(data, ref offset);
            var type = (byte)(header & 0x1F);
            var idBits = (byte)(header >> 5);
            if (type == StopType && idBits == 0)
            {
                if (offset != data.Length)
                    throw new InvalidDataException("Windows returned trailing data in the Night light structure.");
                return fields;
            }
            if (type == StopBaseType && idBits == 0)
            {
                if (!allowTerminalBaseStop || offset != data.Length - 1 || data[offset] != StopType)
                    throw new InvalidDataException("Windows returned an unsupported inherited Night light field.");
                return fields;
            }

            var id = idBits switch
            {
                6 => ReadByte(data, ref offset),
                7 => ReadUInt16(data, ref offset),
                _ => checked((ushort)(previousId + idBits)),
            };
            previousId = id;
            var start = offset;
            SkipValue(data, ref offset, type, 0);
            fields.Add(new Field(id, type, data.AsSpan(start, offset - start).ToArray()));
        }
        throw new InvalidDataException("Windows returned an incomplete Night light structure.");
    }

    private static void SkipStruct(byte[] data, ref int offset, int depth)
    {
        EnsureDepth(depth);
        while (offset < data.Length)
        {
            var header = ReadByte(data, ref offset);
            var type = (byte)(header & 0x1F);
            var idBits = (byte)(header >> 5);
            if (type == StopType && idBits == 0) return;
            if (type == StopBaseType && idBits == 0) continue;
            if (idBits == 6) _ = ReadByte(data, ref offset);
            else if (idBits == 7) _ = ReadUInt16(data, ref offset);
            SkipValue(data, ref offset, type, depth);
        }
        throw new InvalidDataException("Windows returned an incomplete Night light structure.");
    }

    private static void SkipValue(byte[] data, ref int offset, byte type, int depth)
    {
        switch (type)
        {
            case BoolType:
            case 3: // UInt8
            case Int8Type:
                _ = ReadByte(data, ref offset);
                break;
            case 4: // UInt16
            case 5: // UInt32
            case UInt64Type:
            case 15: // Int16
            case Int32Type:
            case 17: // Int64
                _ = ReadVarUInt64(data, ref offset);
                break;
            case 7: // Float
                Advance(data, ref offset, 4);
                break;
            case 8: // Double
                Advance(data, ref offset, 8);
                break;
            case 9: // String
                Advance(data, ref offset, checked((int)ReadVarUInt64(data, ref offset)));
                break;
            case 18: // WString, length is in UTF-16 code units
                Advance(data, ref offset, checked((int)ReadVarUInt64(data, ref offset) * 2));
                break;
            case StructType:
                SkipStruct(data, ref offset, depth + 1);
                break;
            case ListType:
            case 12: // Set
                EnsureDepth(depth);
                var elementType = (byte)(ReadByte(data, ref offset) & 0x1F);
                var count = ReadVarUInt64(data, ref offset);
                if (count > 1_000_000) throw new InvalidDataException("Night light data contains an oversized list.");
                for (ulong i = 0; i < count; i++) SkipValue(data, ref offset, elementType, depth + 1);
                break;
            case 13: // Map
                EnsureDepth(depth);
                var keyType = (byte)(ReadByte(data, ref offset) & 0x1F);
                var valueType = (byte)(ReadByte(data, ref offset) & 0x1F);
                var entries = ReadVarUInt64(data, ref offset);
                if (entries > 1_000_000) throw new InvalidDataException("Night light data contains an oversized map.");
                for (ulong i = 0; i < entries; i++)
                {
                    SkipValue(data, ref offset, keyType, depth + 1);
                    SkipValue(data, ref offset, valueType, depth + 1);
                }
                break;
            default:
                throw new InvalidDataException($"Windows returned an unknown Night light field type ({type}).");
        }
    }

    private static void EnsureDepth(int depth)
    {
        if (depth > 64) throw new InvalidDataException("Night light data is nested too deeply.");
    }

    internal static ulong DecodeVarUInt64(byte[] data)
    {
        var offset = 0;
        var value = ReadVarUInt64(data, ref offset);
        if (offset != data.Length) throw new InvalidDataException("Windows returned an invalid Night light timestamp.");
        return value;
    }

    private static ulong ReadVarUInt64(byte[] data, ref int offset)
    {
        ulong value = 0;
        for (var index = 0; index < 10; index++)
        {
            var next = ReadByte(data, ref offset);
            var part = (ulong)(next & 0x7F);
            var shift = index * 7;
            if (shift == 63 && part > 1) throw new InvalidDataException("Night light data contains an invalid integer.");
            value |= part << shift;
            if (next < 0x80) return value;
        }
        throw new InvalidDataException("Night light data contains an invalid integer.");
    }

    private static byte ReadByte(byte[] data, ref int offset)
    {
        if ((uint)offset >= (uint)data.Length) throw new InvalidDataException("Windows returned truncated Night light data.");
        return data[offset++];
    }

    private static ushort ReadUInt16(byte[] data, ref int offset)
    {
        var low = ReadByte(data, ref offset);
        var high = ReadByte(data, ref offset);
        return (ushort)(low | (high << 8));
    }

    private static void Advance(byte[] data, ref int offset, int count)
    {
        if (count < 0 || offset > data.Length - count)
            throw new InvalidDataException("Windows returned truncated Night light data.");
        offset += count;
    }

    private static Field RequiredField(List<Field> fields, ushort id, byte type, string name)
    {
        var field = fields.FirstOrDefault(candidate => candidate.Id == id);
        if (field.Id != id || field.Type != type)
            throw new InvalidDataException($"Windows returned an unsupported {name}.");
        return field;
    }

    internal static byte[] WriteStateEnvelope(ulong timestamp, byte[] payload)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteMarshaledHeader(writer);

        WriteFieldHeader(writer, 0, StructType);
        WriteFieldHeader(writer, 0, BoolType);
        writer.Write((byte)1);
        writer.Write(StopType);

        WriteFieldHeader(writer, 1, StructType);
        WriteFieldHeader(writer, 0, UInt64Type);
        WriteVarUInt64(writer, timestamp);
        WriteFieldHeader(writer, 1, StructType);
        WriteFieldHeader(writer, 1, ListType);
        writer.Write(Int8Type);
        WriteVarUInt64(writer, (ulong)payload.Length);
        writer.Write(payload);
        writer.Write(StopType);
        writer.Write(StopType);
        writer.Write(StopType);
        return stream.ToArray();
    }

    internal static byte[] WriteCloudRecordEnvelope(ulong timestamp)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteMarshaledHeader(writer);
        WriteFieldHeader(writer, 0, StructType);
        writer.Write(StopType);
        WriteFieldHeader(writer, 1, UInt64Type);
        WriteVarUInt64(writer, timestamp);
        writer.Write(StopType);
        return stream.ToArray();
    }

    internal static byte[] WriteMarshaledStruct(IEnumerable<Field> fields, bool hasBaseStop = false)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteMarshaledHeader(writer);
        foreach (var field in fields)
        {
            WriteFieldHeader(writer, field.Id, field.Type);
            writer.Write(field.Value);
        }
        if (hasBaseStop) writer.Write(StopBaseType);
        writer.Write(StopType);
        return stream.ToArray();
    }

    private static string JoinRegistryPath(params string[] parts) => string.Join("\\", parts);

    internal static StateEnvelope ReadStateEnvelope(byte[] data)
    {
        var outerFields = ReadMarshaledStruct(data);
        var container = RequiredField(outerFields, 1, StructType, "CloudStore payload container");
        var containerFields = ReadStruct(container.Value);
        var timestampField = RequiredField(containerFields, 0, UInt64Type, "CloudStore timestamp");
        var timestamp = DecodeVarUInt64(timestampField.Value);
        var wrapper = RequiredField(containerFields, 1, StructType, "CloudStore data wrapper");
        var wrapperFields = ReadStruct(wrapper.Value);
        var payloadField = RequiredField(wrapperFields, 1, ListType, "CloudStore state payload");
        return new StateEnvelope(timestamp, ReadInt8List(payloadField.Value));
    }

    private static void WriteMarshaledHeader(BinaryWriter writer)
    {
        writer.Write((byte)0x43);
        writer.Write((byte)0x42);
        writer.Write((byte)0x01);
        writer.Write((byte)0x00);
    }

    private static void WriteFieldHeader(BinaryWriter writer, ushort id, byte type)
    {
        if (id < 6)
        {
            writer.Write((byte)((id << 5) | type));
        }
        else if (id <= byte.MaxValue)
        {
            writer.Write((byte)((6 << 5) | type));
            writer.Write((byte)id);
        }
        else
        {
            writer.Write((byte)((7 << 5) | type));
            writer.Write(id);
        }
    }

    internal static byte[] EncodeVarUInt64(ulong value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteVarUInt64(writer, value);
        return stream.ToArray();
    }

    private static void WriteVarUInt64(BinaryWriter writer, ulong value)
    {
        while (value >= 0x80)
        {
            writer.Write((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        writer.Write((byte)value);
    }
}

/// <summary>Changes Windows Night light using state samples captured from this profile.</summary>
