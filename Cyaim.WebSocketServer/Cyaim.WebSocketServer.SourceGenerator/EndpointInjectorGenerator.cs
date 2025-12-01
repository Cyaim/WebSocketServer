using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Cyaim.WebSocketServer.SourceGenerator
{
    /// <summary>
    /// 源代码生成器：为 WebSocket Endpoint 类生成优化的注入器
    /// </summary>
    [Generator]
    public class EndpointInjectorGenerator : ISourceGenerator
    {
        public void Initialize(GeneratorInitializationContext context)
        {
            // 注册语法接收器
            context.RegisterForSyntaxNotifications(() => new EndpointSyntaxReceiver());
        }

        public void Execute(GeneratorExecutionContext context)
        {
            if (!(context.SyntaxContextReceiver is EndpointSyntaxReceiver receiver))
                return;

            // 获取编译
            var compilation = context.Compilation;

            // 获取 WebSocketAttribute 类型
            var webSocketAttributeType = compilation.GetTypeByMetadataName("Cyaim.WebSocketServer.Infrastructure.Attributes.WebSocketAttribute");
            if (webSocketAttributeType == null)
                return;

            // 处理每个候选类
            foreach (var classSymbol in receiver.CandidateClasses)
            {
                var classFullName = classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                var className = classSymbol.Name;
                var namespaceName = classSymbol.ContainingNamespace.ToDisplayString();

                // 查找 HttpContext 和 WebSocket 属性（默认属性名）
                var httpContextProperty = FindProperty(classSymbol, "WebSocketHttpContext");
                var webSocketProperty = FindProperty(classSymbol, "WebSocketClient");

                // 如果两个属性都不存在，跳过
                if (httpContextProperty == null && webSocketProperty == null)
                    continue;

                // 生成注入器代码
                var source = GenerateInjectorCode(namespaceName, className, classFullName, httpContextProperty, webSocketProperty);
                context.AddSource($"{className}Injector.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        }

        private IPropertySymbol FindProperty(INamedTypeSymbol classSymbol, string propertyName)
        {
            return classSymbol.GetMembers(propertyName)
                .OfType<IPropertySymbol>()
                .FirstOrDefault(p => p.SetMethod != null && !p.SetMethod.IsStatic);
        }

        private string GenerateInjectorCode(string namespaceName, string className, string classFullName, IPropertySymbol httpContextProperty, IPropertySymbol webSocketProperty)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using Cyaim.WebSocketServer.Infrastructure.Injectors;");
            sb.AppendLine("using Microsoft.AspNetCore.Http;");
            sb.AppendLine("using System.Net.WebSockets;");
            sb.AppendLine();
            sb.AppendLine($"namespace {namespaceName}");
            sb.AppendLine("{");
            sb.AppendLine($"    /// <summary>");
            sb.AppendLine($"    /// 源代码生成的注入器：{className}");
            sb.AppendLine($"    /// </summary>");
            sb.AppendLine($"    public class {className}Injector : IEndpointInjector");
            sb.AppendLine("    {");
            sb.AppendLine("        public void Inject(object instance, HttpContext httpContext, WebSocket webSocket)");
            sb.AppendLine("        {");
            sb.AppendLine($"            if (instance is {classFullName} target)");
            sb.AppendLine("            {");

            if (httpContextProperty != null)
            {
                sb.AppendLine($"                target.{httpContextProperty.Name} = httpContext;");
            }

            if (webSocketProperty != null)
            {
                sb.AppendLine($"                target.{webSocketProperty.Name} = webSocket;");
            }

            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }
    }

    /// <summary>
    /// 语法接收器：查找带有 WebSocketAttribute 的类
    /// </summary>
    internal class EndpointSyntaxReceiver : ISyntaxContextReceiver
    {
        public List<INamedTypeSymbol> CandidateClasses { get; } = new List<INamedTypeSymbol>();

        public void OnVisitSyntaxNode(GeneratorSyntaxContext context)
        {
            // 查找类声明
            if (context.Node is ClassDeclarationSyntax classDeclaration)
            {
                var symbol = context.SemanticModel.GetDeclaredSymbol(classDeclaration) as INamedTypeSymbol;
                if (symbol == null)
                    return;

                // 检查类中是否有方法带有 WebSocketAttribute
                var hasWebSocketMethod = symbol.GetMembers()
                    .OfType<IMethodSymbol>()
                    .Any(method => method.GetAttributes()
                        .Any(attr => attr.AttributeClass?.Name == "WebSocketAttribute" ||
                                     attr.AttributeClass?.ToDisplayString() == "Cyaim.WebSocketServer.Infrastructure.Attributes.WebSocketAttribute"));

                if (hasWebSocketMethod)
                {
                    CandidateClasses.Add(symbol);
                }
            }
        }
    }
}

