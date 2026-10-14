package app.sofabird.ui.media

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import app.sofabird.R
import app.sofabird.data.player.PlayerController
import app.sofabird.data.settings.SettingsRepository
import app.sofabird.protocol.backend.PlayerCommand
import app.sofabird.protocol.model.LibraryEpisode
import app.sofabird.protocol.model.ShowDetails
import app.sofabird.ui.common.LabelText
import app.sofabird.ui.common.commandErrorMessage
import app.sofabird.ui.common.rememberTick
import app.sofabird.ui.common.withNameBreaks
import app.sofabird.ui.history.WatchProgressBar
import app.sofabird.ui.history.WatchedMark
import coil3.compose.AsyncImage
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import org.koin.androidx.compose.koinViewModel
import org.koin.core.parameter.parametersOf

data class ShowUiState(
    val title: String = "",
    val details: ShowDetails? = null,
    val loading: Boolean = false,
    val error: Throwable? = null,
    /** The chosen season; `null` = the first one. */
    val season: Int? = null,
    val haptics: Boolean = true,
) {
    val seasons: List<Int> get() = details?.episodes?.map { it.season }?.distinct().orEmpty()
    val currentSeason: Int? get() = season?.takeIf { it in seasons } ?: firstUnwatchedSeason ?: seasons.firstOrNull()
    val episodes: List<LibraryEpisode> get() = details?.episodes?.filter { it.season == currentSeason }.orEmpty()
    private val firstUnwatchedSeason: Int? get() = details?.episodes?.firstOrNull { it.file.finished != true }?.season
}

interface ShowActions {
    fun reload() {}
    fun selectSeason(season: Int) {}
    fun play(episode: LibraryEpisode) {}
}

/** One series of the library: `getShow`, again whenever `state.library.revision` changes. */
class ShowViewModel(
    private val key: String,
    private val controller: PlayerController,
    settings: SettingsRepository,
) : ViewModel(), ShowActions {

    private data class Local(
        val details: ShowDetails? = null,
        val loading: Boolean = false,
        val error: Throwable? = null,
        val season: Int? = null,
    )

    private val local = MutableStateFlow(Local())
    private var loadJob: Job? = null

    val uiState: StateFlow<ShowUiState> = combine(local, settings.settings) { l, prefs ->
        ShowUiState(l.details?.show?.title.orEmpty(), l.details, l.loading, l.error, l.season, prefs.haptics)
    }.stateIn(viewModelScope, SharingStarted.WhileSubscribed(5_000), ShowUiState())

    init {
        viewModelScope.launch {
            combine(
                controller.connection.map { it.isOnline }.distinctUntilChanged(),
                controller.state.map { it.library?.revision }.distinctUntilChanged(),
                ::Pair,
            ).collect { (online, _) -> if (online) load() }
        }
    }

    override fun reload() = load()

    override fun selectSeason(season: Int) = local.update { it.copy(season = season) }

    override fun play(episode: LibraryEpisode) = controller.send(PlayerCommand.Open(episode.file.uri))

    private fun load() {
        loadJob?.cancel()
        loadJob = viewModelScope.launch {
            local.update { it.copy(loading = true) }
            val result = controller.show(key)
            local.update { l ->
                result.fold(
                    onSuccess = { l.copy(details = it, loading = false, error = null) },
                    onFailure = { l.copy(loading = false, error = it) },
                )
            }
        }
    }
}

@Composable
fun ShowScreen(
    key: String,
    onBack: () -> Unit,
    onPlayed: () -> Unit,
    viewModel: ShowViewModel = koinViewModel(key = key) { parametersOf(key) },
) {
    val ui by viewModel.uiState.collectAsStateWithLifecycle()
    ShowContent(ui, viewModel, onBack, onPlayed)
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun ShowContent(ui: ShowUiState, actions: ShowActions, onBack: () -> Unit, onPlayed: () -> Unit) {
    val context = LocalContext.current
    val tick = rememberTick(ui.haptics)
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(ui.title, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                navigationIcon = {
                    IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, stringResource(R.string.back)) }
                },
            )
        },
    ) { padding ->
        val details = ui.details
        if (details == null) {
            Box(Modifier.fillMaxSize().padding(padding), contentAlignment = Alignment.Center) {
                if (ui.error != null) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.padding(24.dp)) {
                        Text(ui.error.commandErrorMessage(context), color = MaterialTheme.colorScheme.error, textAlign = TextAlign.Center)
                        TextButton(onClick = actions::reload) { Text(stringResource(R.string.retry)) }
                    }
                } else {
                    CircularProgressIndicator()
                }
            }
            return@Scaffold
        }
        LazyColumn(Modifier.fillMaxSize().padding(padding)) {
            item(key = "header") {
                Column(Modifier.padding(horizontal = 16.dp)) {
                    details.show.backdrop?.let {
                        AsyncImage(
                            model = it,
                            contentDescription = null,
                            contentScale = ContentScale.Crop,
                            modifier = Modifier
                                .fillMaxWidth()
                                .aspectRatio(16f / 9f)
                                .clip(RoundedCornerShape(12.dp))
                                .background(MaterialTheme.colorScheme.surfaceContainerHigh),
                        )
                        Spacer(Modifier.height(12.dp))
                    }
                    details.show.overview?.let {
                        Text(it, style = MaterialTheme.typography.bodyMedium, maxLines = 6, overflow = TextOverflow.Ellipsis)
                        Spacer(Modifier.height(8.dp))
                    }
                    if (ui.seasons.size > 1) {
                        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            ui.seasons.forEach { s ->
                                FilterChip(
                                    selected = s == ui.currentSeason,
                                    onClick = { actions.selectSeason(s) },
                                    label = { LabelText(stringResource(R.string.media_season, s)) },
                                )
                            }
                        }
                    }
                }
            }
            items(ui.episodes, key = { "${it.season}:${it.episode}:${it.file.uri}" }) { episode ->
                EpisodeRow(episode) {
                    tick()
                    actions.play(episode)
                    onPlayed()
                }
            }
            item(key = "tmdb") { TmdbAttribution(Modifier.padding(bottom = 16.dp)) }
        }
    }
}

@Composable
private fun EpisodeRow(episode: LibraryEpisode, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            Modifier
                .width(128.dp)
                .aspectRatio(16f / 9f)
                .clip(RoundedCornerShape(8.dp))
                .background(MaterialTheme.colorScheme.surfaceContainerHigh),
            contentAlignment = Alignment.Center,
        ) {
            Text(
                stringResource(R.string.media_episode, episode.episode),
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            episode.still?.let {
                AsyncImage(model = it, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
            }
            val progress = episode.file.progress?.toFloat()
            if (progress != null && progress > 0f && episode.file.finished != true) {
                WatchProgressBar(progress, Modifier.fillMaxWidth().align(Alignment.BottomCenter))
            }
        }
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(
                stringResource(R.string.media_episode_code, episode.season, episode.episode),
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Text(
                (episode.title ?: episode.file.name).withNameBreaks(),
                style = MaterialTheme.typography.bodyMedium,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
            )
        }
        if (episode.file.finished == true) {
            Spacer(Modifier.width(8.dp))
            WatchedMark()
        }
    }
}
