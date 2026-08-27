using System;
using System.Collections.Generic;
using UnityEngine.Scripting;

[Preserve]
public record ConnectionIdToPc(string DeviceName);

[Preserve]
public record DeviceIdentity(string ConnectionId, string DeviceName);

[Preserve]
public record FileNodeDto(
    string Name,
    string RelativePath,
    bool IsDirectory,
    long Length,
    DateTime LastWriteTimeUtc
);

[Preserve]
public record FileNodeResultDto(
    bool Exists,
    FileNodeDto CurrentNode,
    List<FileNodeDto> Children
);