using System;
using System.Collections.Generic;
using System.Text;

namespace Cyaim.WebSocketServer.Infrastructure.Attributes
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
    public class WebSocketAttribute : Attribute
    {
        public WebSocketAttribute()
        {
        }

        public WebSocketAttribute(string method) : this()
        {
            Method = method;
        }

        public string Method { get; set; }
    }
}
