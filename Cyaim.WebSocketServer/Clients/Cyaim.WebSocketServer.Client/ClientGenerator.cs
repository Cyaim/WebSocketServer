using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text;

namespace Cyaim.WebSocketServer.Client
{
    [Generator]
    public class ClientGenerator : ISourceGenerator
    {
        public void Initialize(GeneratorInitializationContext context)
        {
            // No initialization required
        }

        public void Execute(GeneratorExecutionContext context)
        {
            var compilation = context.Compilation;

            // Find all interfaces
            var interfaces = compilation.SyntaxTrees
                .SelectMany(tree => tree.GetRoot().DescendantNodes())
                .OfType<InterfaceDeclarationSyntax>();

            foreach (var interfaceDeclaration in interfaces)
            {
                var namespaceName = interfaceDeclaration.Parent is NamespaceDeclarationSyntax namespaceDecl ? namespaceDecl.Name.ToString() : "Generated";
                var interfaceName = interfaceDeclaration.Identifier.Text;
                var className = $"{interfaceName}Client";

                var methods = interfaceDeclaration.Members
                    .OfType<MethodDeclarationSyntax>()
                    .Select(method => new
                    {
                        ReturnType = method.ReturnType.ToString(),
                        Name = method.Identifier.Text,
                        Parameters = method.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}")
                    });

                var source = new StringBuilder($@"
using System.Threading.Tasks;
using {namespaceName};

namespace {namespaceName}
{{
    public class {className} : {interfaceName}
    {{
        private readonly WebSocketClient _client;

        public {className}(string serverUri)
        {{
            _client = new WebSocketClient(serverUri);
        }}
");

                foreach (var method in methods)
                {
                    var parameters = string.Join(", ", method.Parameters);
                    source.AppendLine($@"
        public async Task {method.Name}({parameters})
        {{
            // TODO: Implement method
        }}
");
                }

                source.AppendLine("    }\n}");

                context.AddSource($"{className}.g.cs", source.ToString());
            }
        }
    }
}
