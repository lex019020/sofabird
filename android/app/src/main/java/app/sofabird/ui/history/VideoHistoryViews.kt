package app.sofabird.ui.history

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.SmartDisplay
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.sofabird.protocol.model.HistoryItem
import app.sofabird.ui.common.withNameBreaks
import coil3.compose.AsyncImage

/**
 * A video's picture, 16:9: a YouTube thumbnail or a TMDB still (the phone loads it from there itself); [placeholder]
 * until it is there or without one.
 */
@Composable
fun VideoThumbnail(url: String?, modifier: Modifier = Modifier, placeholder: ImageVector = Icons.Default.SmartDisplay) {
    Box(
        modifier
            .aspectRatio(16f / 9f)
            .clip(RoundedCornerShape(8.dp))
            .background(MaterialTheme.colorScheme.surfaceVariant),
        contentAlignment = Alignment.Center,
    ) {
        Icon(placeholder, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
        if (url != null) {
            AsyncImage(model = url, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.matchParentSize())
        }
    }
}

/** "More → YouTube on the TV": the videos played on the TV, newest first; a tap opens one where it stopped. */
@Composable
fun RecentVideosRow(items: List<HistoryItem>, enabled: Boolean, onOpen: (HistoryItem) -> Unit, modifier: Modifier = Modifier) {
    // Cards grow with the font size, so two title lines still fit.
    val cardWidth = (150f * LocalDensity.current.fontScale.coerceIn(1f, 1.5f)).dp
    LazyRow(
        modifier = modifier.fillMaxWidth(),
        contentPadding = PaddingValues(vertical = 4.dp),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        items(items, key = { it.uri }) { item ->
            Column(
                Modifier
                    .width(cardWidth)
                    .clip(RoundedCornerShape(8.dp))
                    .clickable(enabled = enabled) { onOpen(item) },
            ) {
                VideoThumbnail(item.artUrl, Modifier.fillMaxWidth())
                // The bar's place is kept when there is none, so the titles of the row line up.
                Box(Modifier.fillMaxWidth().padding(top = 4.dp).height(4.dp)) {
                    if (item.isStarted) WatchProgressBar(item.fraction.toFloat(), Modifier.fillMaxWidth())
                }
                Text(
                    item.displayTitle.withNameBreaks(),
                    style = MaterialTheme.typography.labelLarge,
                    maxLines = 2,
                    minLines = 2,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(top = 4.dp),
                )
                item.channel?.takeIf { it.isNotBlank() }?.let { channel ->
                    Text(
                        channel,
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }
        }
    }
}
