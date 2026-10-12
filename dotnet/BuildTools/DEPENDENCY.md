# Framework API audit dependency

Mono.Cecil 0.11.6, lib/net40/Mono.Cecil.dll; MIT license in LICENSE_Mono.Cecil.txt.
Source package: https://www.nuget.org/packages/Mono.Cecil/0.11.6
DLL SHA256: `c41bdb9ffd3c5f6e17d2382c1012d73703e035e3f1100245fdd4e08c8dc6eb5b`.

This is an offline development/build dependency. The application never loads it.
The resolver reads only explicit Microsoft Framework reference DLLs or physical Windows Framework DLLs. It never falls back to Mono or resolves against the application's running CLR on Linux.
