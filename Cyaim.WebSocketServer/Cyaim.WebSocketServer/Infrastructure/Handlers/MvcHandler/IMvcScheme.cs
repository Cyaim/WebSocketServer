namespace Cyaim.WebSocketServer.Infrastructure.Handlers.MvcHandler
{
    /// <summary>
    /// MVC Scheme
    /// </summary>
    public interface IMvcScheme
    {
        /// <summary>
        /// Request Id
        /// In Multiplex, you need to keep the id of uniqueness
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Request target
        /// </summary>
        public string Target { get; set; }

        /// <summary>
        /// Request context
        /// </summary>
        public object Body { get; set; }
    }
}
