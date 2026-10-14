package app.sofabird.ui.remote

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedCard
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import app.sofabird.ui.common.LabelText

/** One tile of the remote's quick row: what it opens and, when known, the current value ("Russian", "off"). */
data class QuickAction(
    val icon: ImageVector,
    val title: String,
    val value: String?,
    val enabled: Boolean,
    val onClick: () -> Unit,
)

/**
 * Audio / subtitles / display / more as equal tiles in a single row. Unlike chips with "Audio: Russian" labels,
 * the row never wraps, so it keeps its place above the playback controls at any language and font scale.
 */
@Composable
fun QuickActionsRow(actions: List<QuickAction>, modifier: Modifier = Modifier) {
    Row(
        modifier
            .fillMaxWidth()
            .height(IntrinsicSize.Min)
            .padding(vertical = 8.dp),
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        actions.forEach { QuickActionTile(it, Modifier.weight(1f).fillMaxHeight()) }
    }
}

/** A quarter of a phone width fits "Subtitles" / "Субтитры" only if the labels barely grow with the font. */
private const val TILE_MAX_SCALE = 1.15f

@Composable
private fun QuickActionTile(action: QuickAction, modifier: Modifier) {
    OutlinedCard(onClick = action.onClick, enabled = action.enabled, modifier = modifier) {
        // Centred vertically: "More" has no value line and would otherwise sit higher than its neighbours.
        Column(
            Modifier
                .fillMaxSize()
                .padding(horizontal = 2.dp, vertical = 8.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center,
        ) {
            Icon(action.icon, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(20.dp))
            LabelText(action.title, style = MaterialTheme.typography.labelMedium, textAlign = TextAlign.Center, maxScale = TILE_MAX_SCALE)
            if (action.value != null) {
                LabelText(
                    action.value,
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    textAlign = TextAlign.Center,
                    maxScale = TILE_MAX_SCALE,
                )
            }
        }
    }
}
