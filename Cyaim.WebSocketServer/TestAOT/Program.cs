// See https://aka.ms/new-console-template for more information
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using System.IO;

Console.WriteLine("Hello, World!");


string code = @"
using System;

public class DynamicClass
{
    public void DynamicMethod()
    {
        Console.WriteLine(""Hello from pre-generated code!"");
    }
}";

// 使用 Roslyn 编译代码并保存到磁盘
SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(code);

// 获取程序集位置，对于单文件应用使用 BaseDirectory
#pragma warning disable IL3000 // 'Assembly.Location' always returns an empty string for assemblies embedded in a single-file app
string assemblyLocation = typeof(object).Assembly.Location;
#pragma warning restore IL3000

// 如果 Location 为空（单文件应用），使用 BaseDirectory
if (string.IsNullOrEmpty(assemblyLocation))
{
    assemblyLocation = Path.Combine(AppContext.BaseDirectory, typeof(object).Assembly.GetName().Name + ".dll");
}

CSharpCompilation compilation = CSharpCompilation.Create(
    "DynamicAssembly",
    new[] { syntaxTree },
    new[] { MetadataReference.CreateFromFile(assemblyLocation) },
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

string path = "DynamicAssembly.dll";
EmitResult result = compilation.Emit(path);

if (!result.Success)
{
    foreach (Diagnostic diagnostic in result.Diagnostics)
    {
        Console.WriteLine(diagnostic.ToString());
    }
}
else
{
    Console.WriteLine($"Assembly saved to {path}");
}
