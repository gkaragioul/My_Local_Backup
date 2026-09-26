using MyLocalBackup.Core;
using MyLocalBackup.SafetyTests;

// Data-safety regression tests. Usage:
//   dotnet run -c Release --project tests/MyLocalBackup.SafetyTests [name filter]
// Every test works in its own folder under %TEMP%\mlb-safety-tests and removes it afterwards.

// Keep the engine's logging on the console only, so the real app log in
// %LOCALAPPDATA%\MyLocalBackup\logs.txt is never written or rotated by the tests.
Logger.Shutdown();

var tests = new List<(string Name, Action Body)>();
tests.AddRange(UnitTests.All);
tests.AddRange(EngineTests.All);

return TestRunner.Run(tests, args);
