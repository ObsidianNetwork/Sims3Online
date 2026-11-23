namespace SimsOnline.DedicatedServer.Protocol;

/// <summary>W2 part B: one buff on a mirrored Sim (BuffNames guid + Origin), as the server has it.
/// Carried by <see cref="ServerEvent.SimStateChanged"/>; card 57 ruling 4 moved it to this assembly
/// with its record file's SimStateSample, which parses the bridge wire, staying in DedicatedServer.</summary>
public sealed record SimBuffDto(ulong Guid, ulong Origin);
