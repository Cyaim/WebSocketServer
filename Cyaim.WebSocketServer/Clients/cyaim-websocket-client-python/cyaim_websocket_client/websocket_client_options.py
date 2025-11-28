class WebSocketClientOptions:
    """Options for creating WebSocket client"""
    
    def __init__(
        self,
        validate_all_methods: bool = False,
        lazy_load_endpoints: bool = False,
        throw_on_endpoint_not_found: bool = True
    ):
        self.validate_all_methods = validate_all_methods
        self.lazy_load_endpoints = lazy_load_endpoints
        self.throw_on_endpoint_not_found = throw_on_endpoint_not_found

