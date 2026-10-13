package dev.vlcremote.protocol.vlc

import dev.vlcremote.protocol.model.Entry
import dev.vlcremote.protocol.model.EntryType
import dev.vlcremote.protocol.model.PlaylistItem
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.longOrNull

/**
 * Tolerant parser for the VLC 3.0.x HTTP interface JSON pages. Quirks handled (see
 * protocol/vlc-http.md): `fullscreen` as bool or 0, missing `information` when stopped,
 * `equalizer` `[]` vs object, localized category keys (streams detected by `Type`),
 * track id = `Original_ID` or the number in the key, Lua error pages.
 */
object VlcStatusParser {
    val json: Json = Json {
        ignoreUnknownKeys = true
        isLenient = true
        coerceInputValues = true
    }

    // ---------------------------------------------------------------- status.json

    fun parseStatus(body: String): VlcStatus {
        val root = parseObject(body)
        val information = root["information"]?.asObjectOrNull()?.let(::parseInformation)
        return VlcStatus(
            state = root.str("state") ?: "stopped",
            time = root.long("time") ?: 0L,
            length = root.long("length") ?: 0L,
            position = root.double("position") ?: 0.0,
            rate = root.double("rate") ?: 1.0,
            volume = root.int("volume") ?: 0,
            fullscreen = root["fullscreen"].asFlexibleBoolean(),
            currentPlId = root.int("currentplid") ?: -1,
            audioDelay = root.double("audiodelay") ?: 0.0,
            subtitleDelay = root.double("subtitledelay") ?: 0.0,
            aspectRatio = root.str("aspectratio") ?: "default",
            random = root.bool("random") ?: false,
            loop = root.bool("loop") ?: false,
            repeat = root.bool("repeat") ?: false,
            apiVersion = root.int("apiversion") ?: 3,
            version = root.str("version"),
            seekSec = root.int("seek_sec") ?: 10,
            information = information,
        )
    }

    private fun parseInformation(info: JsonObject): VlcInformation {
        val category = info["category"]?.asObjectOrNull() ?: JsonObject(emptyMap())
        val meta = category["meta"]?.asObjectOrNull()?.let(::parseMeta) ?: VlcMeta()
        val streams = category.filterKeys { it != "meta" }
            .mapNotNull { (key, value) -> value.asObjectOrNull()?.let { parseStream(key, it) } }
            .sortedBy { it.id }
        return VlcInformation(
            chapter = info.int("chapter") ?: -1,
            title = info.int("title") ?: -1,
            chapters = info["chapters"]?.asIntList() ?: chaptersFromCount(info),
            titles = info["titles"]?.asIntList() ?: emptyList(),
            meta = meta,
            streams = streams,
        )
    }

    /** A "Stream N" category; `null` for other categories (stream ones are those with `Type`). */
    private fun parseStream(key: String, obj: JsonObject): VlcStream? {
        val type = obj.str("Type") ?: return null
        val id = obj.int("Original_ID") ?: numberInKey(key) ?: return null
        return VlcStream(
            id = id,
            type = VlcStreamType.parse(type),
            language = obj.str("Language"),
            description = obj.str("Description"),
            codec = obj.str("Codec"),
        )
    }

    /** VLC 4 reports `chapters_count` instead of an index array. */
    private fun chaptersFromCount(info: JsonObject): List<Int> {
        val count = info.int("chapters_count") ?: return emptyList()
        return (0 until count).toList()
    }

    private fun parseMeta(meta: JsonObject): VlcMeta = VlcMeta(
        title = meta.str("title"),
        filename = meta.str("filename"),
        artworkUrl = meta.str("artwork_url"),
        artist = meta.str("artist"),
        album = meta.str("album"),
    )

    private val trailingNumber = Regex("(\\d+)\\s*$")

    private fun numberInKey(key: String): Int? = trailingNumber.find(key)?.groupValues?.get(1)?.toIntOrNull()

    // ---------------------------------------------------------------- playlist.json

    /** Flattens the tree: leaves under the node with id "1" (`Playlist`), `Media Library` ignored. */
    fun parsePlaylist(body: String): List<PlaylistItem> {
        val root = parseObject(body)
        val playlistNode = findPlaylistNode(root) ?: return emptyList()
        val result = mutableListOf<PlaylistItem>()
        collectLeaves(playlistNode, result)
        return result
    }

    private fun findPlaylistNode(root: JsonObject): JsonObject? {
        if (root.str("id") == "1") return root
        val children = root["children"]?.asArrayOrNull() ?: return null
        for (child in children) {
            val obj = child.asObjectOrNull() ?: continue
            if (obj.str("id") == "1") return obj
        }
        // Fallback: the first node that is not the Media Library.
        return children.mapNotNull { it.asObjectOrNull() }
            .firstOrNull { it.str("type") == "node" && it.str("name") != "Media Library" }
    }

    private fun collectLeaves(node: JsonObject, out: MutableList<PlaylistItem>) {
        val children = node["children"]?.asArrayOrNull() ?: return
        for (child in children) {
            val obj = child.asObjectOrNull() ?: continue
            when (obj.str("type")) {
                "leaf" -> {
                    val id = obj.int("id") ?: continue
                    out += PlaylistItem(
                        plId = id,
                        name = obj.str("name") ?: "",
                        uri = obj.str("uri") ?: "",
                        durationSec = obj.long("duration")?.coerceAtLeast(0) ?: 0,
                        current = obj.str("current") == "current",
                    )
                }
                "node" -> collectLeaves(obj, out)
            }
        }
    }

    // ---------------------------------------------------------------- browse.json

    /** `..` is skipped; the caller builds "up" itself. Uses `uri`, never the Windows `path`. */
    fun parseBrowse(body: String): List<Entry> {
        val root = parseObject(body)
        val elements = root["element"]?.asArrayOrNull() ?: return emptyList()
        return elements.mapNotNull { el ->
            val obj = el.asObjectOrNull() ?: return@mapNotNull null
            val name = obj.str("name") ?: return@mapNotNull null
            if (name == "..") return@mapNotNull null
            val uri = obj.str("uri") ?: return@mapNotNull null
            Entry(
                name = name,
                uri = uri,
                type = when (obj.str("type")) {
                    "dir" -> EntryType.DIR
                    "file" -> EntryType.FILE
                    else -> EntryType.UNKNOWN
                },
                size = obj.long("size"),
                modified = obj.long("modification_time"),
            )
        }
    }

    // ---------------------------------------------------------------- tracks.json (level 1)

    fun parseTracks(body: String): VlcTracksPage {
        val root = parseObject(body)
        fun list(name: String): VlcSelectList? = root[name]?.asObjectOrNull()?.let(::parseSelectList)
        return VlcTracksPage(
            audio = list("audio"),
            spu = list("spu"),
            video = list("video"),
            chapter = list("chapter"),
            title = list("title"),
            aspect = list("aspect"),
            crop = list("crop"),
            zoom = list("zoom"),
            deinterlace = list("deinterlace"),
            stereoMode = list("stereo_mode"),
        )
    }

    private fun parseSelectList(obj: JsonObject): VlcSelectList {
        val items = obj["items"]?.asArrayOrNull()?.mapNotNull { el ->
            val item = el.asObjectOrNull() ?: return@mapNotNull null
            val id = item["id"]?.asPrimitiveOrNull()?.contentOrNull ?: return@mapNotNull null
            VlcSelectItem(id = id, name = item.str("name") ?: "")
        } ?: emptyList()
        return VlcSelectList(
            current = obj["current"]?.asPrimitiveOrNull()?.contentOrNull,
            items = items,
        )
    }

    /** Lua page names end with ` - [Language]`; split the language out. */
    fun splitTrackName(raw: String): Pair<String, String?> {
        val m = languageSuffix.find(raw) ?: return raw.trim() to null
        val name = raw.substring(0, m.range.first).trim()
        val lang = m.groupValues[1].trim().ifEmpty { null }
        return name to lang
    }

    private val languageSuffix = Regex("\\s*-\\s*\\[([^\\]]*)]\\s*$")

    // ---------------------------------------------------------------- helpers

    private fun parseObject(body: String): JsonObject {
        if (body.isBlank()) throw VlcParseException("empty body")
        VlcErrorPage.check(body)
        return try {
            json.parseToJsonElement(body).jsonObject
        } catch (e: SerializationException) {
            throw VlcParseException("not JSON", e)
        } catch (e: IllegalArgumentException) {
            throw VlcParseException("not a JSON object", e)
        }
    }

    private fun JsonElement?.asObjectOrNull(): JsonObject? = this as? JsonObject
    private fun JsonElement?.asArrayOrNull(): JsonArray? = this as? JsonArray
    private fun JsonElement?.asPrimitiveOrNull(): JsonPrimitive? = this as? JsonPrimitive

    private fun JsonObject.str(key: String): String? = this[key].asPrimitiveOrNull()?.let {
        if (it is JsonNull) null else it.contentOrNull
    }

    private fun JsonObject.int(key: String): Int? = this[key].asPrimitiveOrNull()?.let {
        it.intOrNull ?: it.doubleOrNull?.toInt() ?: it.contentOrNull?.trim()?.toIntOrNull()
    }

    private fun JsonObject.long(key: String): Long? = this[key].asPrimitiveOrNull()?.let {
        it.longOrNull ?: it.doubleOrNull?.toLong() ?: it.contentOrNull?.trim()?.toLongOrNull()
    }

    private fun JsonObject.double(key: String): Double? = this[key].asPrimitiveOrNull()?.let {
        it.doubleOrNull ?: it.contentOrNull?.trim()?.toDoubleOrNull()
    }

    private fun JsonObject.bool(key: String): Boolean? = this[key].asFlexibleBooleanOrNull()

    private fun JsonElement?.asFlexibleBoolean(): Boolean = asFlexibleBooleanOrNull() ?: false

    private fun JsonElement?.asFlexibleBooleanOrNull(): Boolean? {
        val p = asPrimitiveOrNull() ?: return null
        if (p is JsonNull) return null
        p.booleanOrNull?.let { return it }
        p.intOrNull?.let { return it != 0 }
        p.doubleOrNull?.let { return it != 0.0 }
        return when (p.contentOrNull?.lowercase()) {
            "true", "1", "yes" -> true
            "false", "0", "no", "" -> false
            else -> null
        }
    }

    private fun JsonElement.asIntList(): List<Int>? {
        val arr = asArrayOrNull() ?: return null
        return arr.mapNotNull { (it as? JsonPrimitive)?.intOrNull }
    }
}
