package app.sofabird.ui.theme

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import app.sofabird.domain.model.AppSettings
import app.sofabird.domain.model.ThemeMode

private val Orange = Color(0xFFFF8A3D)
private val OrangeDim = Color(0xFFE8641B)
private val OnOrange = Color(0xFF3A1600)
private val Cream = Color(0xFFFFDBC8)

private val DarkScheme = darkColorScheme(
    primary = Orange,
    onPrimary = OnOrange,
    primaryContainer = Color(0xFF7A3200),
    onPrimaryContainer = Cream,
    secondary = Color(0xFFE2BFA8),
    onSecondary = Color(0xFF422B1C),
    secondaryContainer = Color(0xFF5B4131),
    onSecondaryContainer = Cream,
    tertiary = Color(0xFFCCC88F),
    background = Color(0xFF141210),
    onBackground = Color(0xFFEDE0D8),
    surface = Color(0xFF141210),
    onSurface = Color(0xFFEDE0D8),
    surfaceVariant = Color(0xFF52443C),
    onSurfaceVariant = Color(0xFFD7C2B8),
    outline = Color(0xFFA08D84),
    error = Color(0xFFFFB4AB),
    onError = Color(0xFF690005),
)

private val LightScheme = lightColorScheme(
    primary = OrangeDim,
    onPrimary = Color.White,
    primaryContainer = Cream,
    onPrimaryContainer = Color(0xFF331100),
    secondary = Color(0xFF765848),
    onSecondary = Color.White,
    secondaryContainer = Cream,
    onSecondaryContainer = Color(0xFF2C160A),
    background = Color(0xFFFFF8F5),
    onBackground = Color(0xFF221A15),
    surface = Color(0xFFFFF8F5),
    onSurface = Color(0xFF221A15),
    surfaceVariant = Color(0xFFF4DED4),
    onSurfaceVariant = Color(0xFF52443C),
    outline = Color(0xFF85736B),
)

private fun ColorScheme.trueBlack(): ColorScheme = copy(
    background = Color.Black,
    surface = Color.Black,
    surfaceContainerLowest = Color.Black,
    surfaceContainerLow = Color(0xFF0A0A0A),
    surfaceContainer = Color(0xFF111111),
    surfaceContainerHigh = Color(0xFF181818),
    surfaceContainerHighest = Color(0xFF202020),
)

@Composable
fun SofabirdTheme(settings: AppSettings, content: @Composable () -> Unit) {
    val dark = when (settings.theme) {
        ThemeMode.SYSTEM -> isSystemInDarkTheme()
        ThemeMode.DARK -> true
        ThemeMode.LIGHT -> false
    }
    val context = LocalContext.current
    val dynamic = settings.dynamicColor && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S
    var scheme = when {
        dynamic && dark -> dynamicDarkColorScheme(context)
        dynamic -> dynamicLightColorScheme(context)
        dark -> DarkScheme
        else -> LightScheme
    }
    if (dark && settings.trueBlack) scheme = scheme.trueBlack()
    MaterialTheme(colorScheme = scheme, content = content)
}
