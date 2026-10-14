package app.sofabird.protocol.companion

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Test

class HelloLocaleTest {
    @Test
    fun `hello carries the phone locale`() {
        val line = CompanionCodec.encode(
            CompanionMessage.Hello(device = DeviceInfo(name = "Galaxy S24", id = "abc", locale = "ru-RU")),
        )
        val device = Json.parseToJsonElement(line).jsonObject["device"]!!.jsonObject
        assertEquals("ru-RU", device["locale"]!!.jsonPrimitive.content)
    }

    @Test
    fun `device info without locale stays compatible`() {
        assertEquals(null, DeviceInfo(name = "x", id = "y").locale)
    }
}
