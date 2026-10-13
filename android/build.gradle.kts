plugins {
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.kotlin.android) apply false
    alias(libs.plugins.kotlin.jvm) apply false
    alias(libs.plugins.kotlin.compose) apply false
    alias(libs.plugins.kotlin.serialization) apply false
    alias(libs.plugins.roborazzi) apply false
    alias(libs.plugins.detekt)
}

// Static analysis of every module: detekt's default rules plus the ktlint formatting rules, with the project's
// adjustments in config/detekt.yml. `gradlew detekt` checks, `gradlew detekt --auto-correct` fixes formatting.
subprojects {
    apply(plugin = "io.gitlab.arturbosch.detekt")
    extensions.configure<io.gitlab.arturbosch.detekt.extensions.DetektExtension> {
        buildUponDefaultConfig = true
        config.setFrom(rootProject.file("config/detekt.yml"))
        parallel = true
    }
    dependencies {
        add("detektPlugins", rootProject.libs.detekt.formatting)
    }
    // detekt runs on the Kotlin compiler it was built with, not the project's.
    configurations.matching { it.name == "detekt" }.configureEach {
        resolutionStrategy.eachDependency {
            if (requested.group == "org.jetbrains.kotlin") useVersion("2.0.10")
        }
    }
}
