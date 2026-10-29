package app.sofabird.ui.common

import androidx.compose.material3.LocalTextStyle
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.isSpecified

/**
 * Largest font scale for labels inside fixed-size controls (tabs, buttons, chips, the top bar): above it the
 * label stops growing, so a large system font does not break it letter by letter. Body text is not capped.
 */
const val CONTROL_LABEL_MAX_SCALE = 1.3f

/** This style with its growth under the system font scale stopped at [maxScale]. */
@Composable
fun TextStyle.cappedScale(maxScale: Float = CONTROL_LABEL_MAX_SCALE): TextStyle {
    val scale = LocalDensity.current.fontScale
    if (scale <= maxScale || !fontSize.isSpecified) return this
    val factor = maxScale / scale
    return copy(
        fontSize = fontSize * factor,
        lineHeight = if (lineHeight.isSpecified) lineHeight * factor else lineHeight,
    )
}

/**
 * A one-line label for buttons, chips, tabs and badges: never wraps (so never breaks inside a word),
 * truncates with an ellipsis, and grows with the system font scale only up to [maxScale].
 */
@Composable
fun LabelText(
    text: String,
    modifier: Modifier = Modifier,
    style: TextStyle = LocalTextStyle.current,
    color: Color = Color.Unspecified,
    textAlign: TextAlign? = null,
    maxScale: Float = CONTROL_LABEL_MAX_SCALE,
) {
    Text(
        text,
        modifier = modifier,
        style = style.cappedScale(maxScale),
        color = color,
        textAlign = textAlign,
        maxLines = 1,
        softWrap = false,
        overflow = TextOverflow.Ellipsis,
    )
}

/**
 * File names like `The.Movie.2001.2160p.mkv` have no spaces, so a line can only break inside a word.
 * A zero-width space after dots, underscores and colons gives the text layout clean break points (also for
 * addresses such as `192.168.1.30:47555`).
 */
fun String.withNameBreaks(): String = replace(".", ".\u200B").replace("_", "_\u200B").replace(":", ":\u200B")

/**
 * A Windows path (`D:\Movies`, `\\server\share`) that wraps after a separator, never between the two backslashes
 * that start a network path.
 */
fun String.withPathBreaks(): String {
    val network = startsWith("\\\\")
    val rest = (if (network) substring(2) else this).replace("\\", "\\\u200B")
    return if (network) "\\\u2060\\\u2060" + rest else rest
}
