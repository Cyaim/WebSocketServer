# cyaim-websocket-client-java

WebSocket client for Cyaim.WebSocketServer with automatic endpoint discovery (Java)

## Installation

Add to your `pom.xml`:

```xml
<dependency>
    <groupId>com.cyaim</groupId>
    <artifactId>websocket-client</artifactId>
    <version>1.0.0</version>
</dependency>
```

## Usage

### Basic Example

```java
import com.cyaim.websocket.*;

public class Example {
    public static void main(String[] args) throws Exception {
        WebSocketClientFactory factory = new WebSocketClientFactory(
            "http://localhost:5000",
            "/ws",
            new WebSocketClientOptions()
        );

        WebSocketClient client = factory.createClient();
        client.connect().get();

        // Get forecasts
        List<WeatherForecast> forecasts = client.sendRequest(
            "weatherforecast.get",
            null,
            new TypeToken<List<WeatherForecast>>(){}.getType()
        ).get();

        // Get forecast by city
        Map<String, String> requestBody = new HashMap<>();
        requestBody.put("city", "Beijing");
        WeatherForecast forecast = client.sendRequest(
            "weatherforecast.getbycity",
            requestBody,
            WeatherForecast.class
        ).get();
    }
}
```

## License

MIT

