package app.sofabird.data.update

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/** A release in the GitHub API (`GET /repos/{owner}/{repo}/releases`), the fields the app needs. */
@Serializable
data class GitHubRelease(
    @SerialName("tag_name") val tag: String,
    val prerelease: Boolean = false,
    val draft: Boolean = false,
    @SerialName("html_url") val page: String = "",
    val body: String? = null,
    val assets: List<GitHubAsset> = emptyList(),
)

@Serializable
data class GitHubAsset(
    val name: String,
    val size: Long = 0,
    @SerialName("browser_download_url") val url: String,
)

/**
 * A newer build to install. [noteworthy] — at least one build since the installed one changed something users notice
 * (a docs-only merge also makes a test build: the app then does not ask, the settings still show it).
 */
data class AvailableUpdate(
    val version: String,
    val apkUrl: String,
    val size: Long,
    val page: String,
    val noteworthy: Boolean,
)

/** Picks the update from the GitHub releases of the project (pure). */
object ReleaseFeed {
    const val URL = "https://api.github.com/repos/lex019020/sofabird/releases?per_page=30"

    /** What `scripts/release_notes.py` writes when no merged PR has release notes. */
    private const val NO_CHANGES = "No user-visible changes."

    private val json = Json { ignoreUnknownKeys = true }

    fun parse(text: String): List<GitHubRelease> = json.decodeFromString(text)

    /** The newest release of [channel] that is newer than [installed] and has the app's APK, or `null`. */
    fun newest(releases: List<GitHubRelease>, channel: UpdateChannel, installed: AppVersion): AvailableUpdate? {
        val newer = releases
            .filter { !it.draft && (channel == UpdateChannel.TEST || !it.prerelease) }
            .mapNotNull { release -> AppVersion.parse(release.tag)?.let { release to it } }
            .filter { (release, version) -> version > installed && release.apk(version) != null }
            .sortedByDescending { it.second }
        val (release, version) = newer.firstOrNull() ?: return null
        val apk = release.apk(version) ?: return null
        return AvailableUpdate(
            version = version.toString(),
            apkUrl = apk.url,
            size = apk.size,
            page = release.page,
            noteworthy = newer.any { (r, _) -> !r.body.orEmpty().trimStart().startsWith(NO_CHANGES) },
        )
    }

    /** `Sofabird-<version>.apk`, as the release workflow names it. */
    private fun GitHubRelease.apk(version: AppVersion): GitHubAsset? = assets.firstOrNull { it.name == "Sofabird-$version.apk" }
}
