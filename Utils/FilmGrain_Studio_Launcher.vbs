Option Explicit

Dim args, fso, shell, scriptPath, commandLine, i, rc
Set args = WScript.Arguments

If args.Count < 1 Then
    MsgBox "Film Grain Studio PowerShell path was not supplied.", 16, "Film Grain Studio"
    WScript.Quit 1
End If

scriptPath = args(0)
Set fso = CreateObject("Scripting.FileSystemObject")

If Not fso.FileExists(scriptPath) Then
    MsgBox "Film Grain Studio PowerShell file was not found:" & vbCrLf & scriptPath, 16, "Film Grain Studio"
    WScript.Quit 1
End If

commandLine = "powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File " & QuoteArgument(scriptPath)
For i = 1 To args.Count - 1
    commandLine = commandLine & " " & QuoteArgument(args(i))
Next

Set shell = CreateObject("WScript.Shell")
rc = shell.Run(commandLine, 0, False)
WScript.Quit rc

Function QuoteArgument(ByVal value)
    QuoteArgument = Chr(34) & Replace(CStr(value), Chr(34), Chr(34) & Chr(34)) & Chr(34)
End Function
