using System.Reflection;

// 经典格式 csproj（DataAssistant.Classic.csproj）不会自动生成程序集版本特性，
// 必须手写本文件，标题栏 AppVersionText() 也从这里读取版本号。
// SDK 风格工程（DataAssistant.csproj）会在其 csproj 中 <Compile Remove> 排除本文件，
// 由 GenerateAssemblyInfo 自行生成，避免重复特性。
// 升版本号时：同步改这里 + 两个 csproj 的 <AssemblyVersion>/<FileVersion> + InstallerCore.AppVersion。
[assembly: AssemblyVersion("1.0.3.0")]
[assembly: AssemblyFileVersion("1.0.3.0")]
