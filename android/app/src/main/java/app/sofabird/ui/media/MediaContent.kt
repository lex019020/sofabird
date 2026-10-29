package app.sofabird.ui.media

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyGridScope
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.layout
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import app.sofabird.R
import app.sofabird.protocol.model.LibraryMovie
import app.sofabird.protocol.model.LibraryShow
import app.sofabird.protocol.model.MediaFile
import app.sofabird.ui.common.LabelText
import app.sofabird.ui.common.commandErrorMessage
import app.sofabird.ui.common.formatSize
import app.sofabird.ui.common.rememberTick
import app.sofabird.ui.common.withNameBreaks
import app.sofabird.ui.common.withPathBreaks
import app.sofabird.ui.history.WatchProgressBar
import app.sofabird.ui.history.WatchedMark
import coil3.compose.AsyncImage
import java.util.Locale

/** "Posters | Folders" at the top of the Library tab (only with a companion library). */
@Composable
fun LibraryViewSwitch(posters: Boolean, onChange: (Boolean) -> Unit, modifier: Modifier = Modifier) {
    SingleChoiceSegmentedButtonRow(modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 6.dp)) {
        SegmentedButton(
            selected = posters,
            onClick = { onChange(true) },
            shape = SegmentedButtonDefaults.itemShape(0, 2),
        ) { LabelText(stringResource(R.string.media_view_posters)) }
        SegmentedButton(
            selected = !posters,
            onClick = { onChange(false) },
            shape = SegmentedButtonDefaults.itemShape(1, 2),
        ) { LabelText(stringResource(R.string.media_view_folders)) }
    }
}

/**
 * The library as posters: movies, then series. [header] is drawn above the grid's own controls (the view switch,
 * "Continue watching"). Tap a movie → its card; tap a series → [onOpenShow].
 */
@Composable
fun MediaContent(
    ui: MediaUiState,
    actions: MediaActions,
    onOpenShow: (LibraryShow) -> Unit,
    onPlayed: () -> Unit,
    header: @Composable () -> Unit = {},
) {
    val context = LocalContext.current
    val tick = rememberTick(ui.haptics)
    LazyVerticalGrid(
        columns = GridCells.Adaptive(minSize = 104.dp),
        contentPadding = PaddingValues(start = GridPadding, end = GridPadding, bottom = 16.dp),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
        modifier = Modifier.fillMaxSize(),
    ) {
        fullWidth("header") { Column(Modifier.bleed(GridPadding)) { header() } }
        fullWidth("filters") { Filters(ui, actions) }
        if (ui.missingFolders.isNotEmpty()) {
            fullWidth("missing") {
                Text(
                    stringResource(R.string.media_missing_folders, ui.missingFolders.joinToString(", ") { it.withPathBreaks() }),
                    color = MaterialTheme.colorScheme.error,
                    style = MaterialTheme.typography.bodyMedium,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 8.dp),
                )
            }
        }
        when {
            ui.error != null && !ui.loaded -> fullWidth("error") {
                Column(Modifier.fillMaxWidth().padding(24.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                    Text(ui.error.commandErrorMessage(context), color = MaterialTheme.colorScheme.error, textAlign = TextAlign.Center)
                    TextButton(onClick = actions::reload) { Text(stringResource(R.string.retry)) }
                }
            }
            !ui.loaded -> fullWidth("loading") {
                Box(Modifier.fillMaxWidth().padding(32.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
            }
            ui.isEmpty && !ui.scanning && ui.missingFolders.isNotEmpty() -> Unit // the line above says why
            ui.isEmpty && !ui.scanning -> fullWidth("empty") {
                Text(
                    stringResource(R.string.media_empty),
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.fillMaxWidth().padding(24.dp),
                )
            }
            else -> {
                if (ui.visibleMovies.isNotEmpty()) {
                    if (ui.filter == MediaFilter.ALL) fullWidth("movies") { SectionTitle(stringResource(R.string.media_movies)) }
                    items(ui.visibleMovies, key = { "m:" + it.key }) { movie ->
                        PosterCard(
                            title = movie.title,
                            poster = movie.poster,
                            caption = movie.year?.toString(),
                            progress = movie.files.firstOrNull()?.progress?.toFloat(),
                            finished = movie.files.any { it.finished == true },
                            onClick = { tick(); actions.openMovie(movie) },
                        )
                    }
                }
                if (ui.visibleShows.isNotEmpty()) {
                    if (ui.filter == MediaFilter.ALL) fullWidth("shows") { SectionTitle(stringResource(R.string.media_shows)) }
                    items(ui.visibleShows, key = { "s:" + it.key }) { show ->
                        PosterCard(
                            title = show.title,
                            poster = show.poster,
                            caption = pluralStringResource(R.plurals.media_episodes, show.episodes, show.episodes),
                            progress = null,
                            finished = show.episodes > 0 && show.unwatched == 0,
                            badge = show.unwatched.takeIf { it in 1 until show.episodes },
                            onClick = { tick(); onOpenShow(show) },
                        )
                    }
                }
            }
        }
        if (ui.loaded && !ui.isEmpty) fullWidth("tmdb") { TmdbAttribution() }
    }

    ui.openMovie?.let { movie ->
        MovieSheet(
            movie = movie,
            onDismiss = { actions.openMovie(null) },
            onPlay = { file ->
                tick()
                actions.openMovie(null)
                actions.play(file)
                onPlayed()
            },
        )
    }
}

private val GridPadding = 12.dp

private fun LazyGridScope.fullWidth(key: String, content: @Composable () -> Unit) =
    item(key = key, span = { GridItemSpan(maxLineSpan) }) { content() }

/** Draws edge to edge through the grid's side padding (the view switch and "Continue watching" have their own). */
private fun Modifier.bleed(horizontal: Dp) = layout { measurable, constraints ->
    val extra = (horizontal * 2).roundToPx()
    val placeable = measurable.measure(
        constraints.copy(minWidth = constraints.minWidth + extra, maxWidth = constraints.maxWidth + extra),
    )
    layout(constraints.maxWidth, placeable.height) { placeable.place(-extra / 2, 0) }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun Filters(ui: MediaUiState, actions: MediaActions) {
    Column {
        FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            MediaFilter.entries.forEach { f ->
                FilterChip(
                    selected = ui.filter == f,
                    onClick = { actions.setFilter(f) },
                    label = {
                        LabelText(
                            stringResource(
                                when (f) {
                                    MediaFilter.ALL -> R.string.media_filter_all
                                    MediaFilter.MOVIES -> R.string.media_movies
                                    MediaFilter.SHOWS -> R.string.media_shows
                                },
                            ),
                        )
                    },
                )
            }
        }
        if (ui.scanning) {
            Text(
                stringResource(R.string.media_scanning),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(top = 4.dp),
            )
            LinearProgressIndicator(Modifier.fillMaxWidth().padding(top = 4.dp))
        }
    }
}

@Composable
private fun SectionTitle(text: String) {
    Text(text, style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(top = 8.dp))
}

/** A 2:3 poster (or a placeholder with the title) with the title and a caption under it. */
@Composable
fun PosterCard(
    title: String,
    poster: String?,
    caption: String?,
    progress: Float?,
    finished: Boolean,
    onClick: () -> Unit,
    badge: Int? = null,
) {
    Column(Modifier.clip(RoundedCornerShape(8.dp)).clickable(onClick = onClick)) {
        Box(
            Modifier
                .fillMaxWidth()
                .aspectRatio(2f / 3f)
                .clip(RoundedCornerShape(8.dp))
                .background(MaterialTheme.colorScheme.surfaceContainerHigh),
        ) {
            // The title stays underneath: it shows while the poster loads and when there is none.
            Text(
                title.withNameBreaks(),
                style = MaterialTheme.typography.labelLarge,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center,
                maxLines = 5,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.align(Alignment.Center).padding(8.dp),
            )
            if (poster != null) {
                AsyncImage(model = poster, contentDescription = title, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
            }
            if (finished) WatchedMark(Modifier.align(Alignment.TopEnd).padding(4.dp))
            badge?.let {
                Surface(
                    color = MaterialTheme.colorScheme.primary,
                    contentColor = MaterialTheme.colorScheme.onPrimary,
                    shape = RoundedCornerShape(6.dp),
                    modifier = Modifier.align(Alignment.TopEnd).padding(4.dp),
                ) {
                    Text(
                        pluralStringResource(R.plurals.media_unwatched, it, it),
                        style = MaterialTheme.typography.labelSmall,
                        maxLines = 1,
                        modifier = Modifier.padding(horizontal = 5.dp, vertical = 1.dp),
                    )
                }
            }
            if (progress != null && progress > 0f && !finished) {
                WatchProgressBar(progress, Modifier.fillMaxWidth().align(Alignment.BottomCenter))
            }
        }
        Text(
            title.withNameBreaks(),
            style = MaterialTheme.typography.bodySmall,
            maxLines = 2,
            minLines = 2,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(top = 4.dp),
        )
        if (caption != null) {
            Text(
                caption,
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

@Composable
fun TmdbAttribution(modifier: Modifier = Modifier) {
    Text(
        stringResource(R.string.media_tmdb),
        style = MaterialTheme.typography.labelSmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        textAlign = TextAlign.Center,
        modifier = modifier.fillMaxWidth().padding(top = 16.dp, start = 8.dp, end = 8.dp),
    )
}

/** Backdrop, title, year and rating, description, "Play" and the versions when there are several files. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MovieSheet(movie: LibraryMovie, onDismiss: () -> Unit, onPlay: (MediaFile) -> Unit) {
    ModalBottomSheet(onDismissRequest = onDismiss) {
        MovieDetails(movie, onPlay)
    }
}

@Composable
fun MovieDetails(movie: LibraryMovie, onPlay: (MediaFile) -> Unit) {
    val context = LocalContext.current
    Column(
        Modifier
            .fillMaxWidth()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 20.dp, vertical = 4.dp),
    ) {
        movie.backdrop?.let {
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
        Text(movie.title.withNameBreaks(), style = MaterialTheme.typography.titleLarge)
        val meta = listOfNotNull(
            movie.year?.toString(),
            movie.rating?.let { stringResource(R.string.media_rating, String.format(Locale.getDefault(), "%.1f", it)) },
        ).joinToString(" · ")
        if (meta.isNotEmpty()) {
            Text(meta, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        val main = movie.files.firstOrNull()
        if (main != null) {
            Spacer(Modifier.height(12.dp))
            Button(onClick = { onPlay(main) }, modifier = Modifier.fillMaxWidth()) {
                Icon(Icons.Default.PlayArrow, contentDescription = null)
                Spacer(Modifier.width(8.dp))
                LabelText(stringResource(R.string.action_play))
            }
            val progress = main.progress?.toFloat()
            if (progress != null && progress > 0f && main.finished != true) {
                WatchProgressBar(progress, Modifier.fillMaxWidth().padding(top = 8.dp))
            }
        }
        movie.overview?.let {
            Spacer(Modifier.height(12.dp))
            Text(it, style = MaterialTheme.typography.bodyMedium)
        }
        if (movie.files.size > 1) {
            Spacer(Modifier.height(12.dp))
            Text(stringResource(R.string.media_versions), style = MaterialTheme.typography.titleSmall)
            movie.files.forEach { file ->
                ListItem(
                    headlineContent = { Text(file.name.withNameBreaks(), maxLines = 2, overflow = TextOverflow.Ellipsis) },
                    supportingContent = { Text(formatSize(context, file.size)) },
                    trailingContent = { if (file.finished == true) WatchedMark() },
                    modifier = Modifier.clickable { onPlay(file) },
                )
            }
        }
        Spacer(Modifier.height(24.dp))
    }
}
