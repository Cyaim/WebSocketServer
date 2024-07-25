// See https://aka.ms/new-console-template for more information
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

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
CSharpCompilation compilation = CSharpCompilation.Create(
    "DynamicAssembly",
    new[] { syntaxTree },
    new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
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
