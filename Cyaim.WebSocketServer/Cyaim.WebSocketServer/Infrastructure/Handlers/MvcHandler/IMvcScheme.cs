namespace Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler
{
    /// <summary>
    /// MVC Scheme
    /// </summary>
    public interface IMvcScheme
    {
        /// <summary>
        /// Request/Response Id
        /// In Multiplex, you need to keep the id of uniqueness
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Target
        /// </summary>
        public string Target { get; set; }

        /// <summary>
        /// Request/Response context
        /// </summary>
        public object Body { get; set; }

        public const string VAR_TATGET = "target";
    }
}
