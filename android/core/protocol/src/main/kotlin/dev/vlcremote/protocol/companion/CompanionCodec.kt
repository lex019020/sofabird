package dev.vlcremote.protocol.companion

import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull

/** Encodes/decodes one JSON message per WebSocket frame (or NDJSON line). */
object CompanionCodec {
    val json: Json = Json {
        ignoreUnknownKeys = true
        isLenient = true
        coerceInputValues = true
        encodeDefaults = true
        explicitNulls = true
        classDiscriminator = "t"
    }

    fun encode(message: CompanionMessage): String = json.encodeToString(CompanionMessage.serializer(), message)

    /** NDJSON form (`\n`-terminated), accepted by both transports. */
    fun encodeLine(message: CompanionMessage): String = encode(message) + "\n"

    /**
     * Decodes a frame. Returns `null` for blank input. Unknown `t` values are tolerated and
     * returned as [DecodedMessage.Unknown] so a newer companion does not break the phone.
     */
    fun decode(text: String): DecodedMessage? {
        val trimmed = text.trim()
        if (trimmed.isEmpty()) return null
        return try {
            DecodedMessage.Known(json.decodeFromString(CompanionMessage.serializer(), trimmed))
        } catch (e: SerializationException) {
            val type = runCatching {
                (json.parseToJsonElement(trimmed) as? JsonObject)?.get("t")?.let { it as? JsonPrimitive }?.contentOrNull
            }.getOrNull()
            if (type != null) DecodedMessage.Unknown(type, trimmed) else throw e
        }
    }

    fun decodeInfo(text: String): CompanionInfo = json.decodeFromString(CompanionInfo.serializer(), text)
}

sealed class DecodedMessage {
    data class Known(val message: CompanionMessage) : DecodedMessage()
    data class Unknown(val type: String, val raw: String) : DecodedMessage()
}
