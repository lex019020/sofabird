import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
    alias(libs.plugins.roborazzi)
}

// Version: the base comes from the VERSION file in the repository root; CI/the deploy script pass
// -Psofabird.versionCode=<monotonic number> and -Psofabird.versionSuffix=beta<NNNN> (a release has no suffix).
val baseVersion = rootProject.file("../VERSION").readText().trim()
val versionSuffix = (findProperty("sofabird.versionSuffix") as String?).orEmpty()
val appVersionCode = (findProperty("sofabird.versionCode") as String?)?.toInt() ?: 1
val appVersionName = if (versionSuffix.isEmpty()) baseVersion else "$baseVersion-$versionSuffix"

// Signing of release builds (test builds and releases): android/keystore.properties (not in git) or
// the environment variables SOFABIRD_KEYSTORE_FILE / SOFABIRD_KEYSTORE_PASSWORD / SOFABIRD_KEY_ALIAS / SOFABIRD_KEY_PASSWORD.
// One key for all channels so that updates install over the previous version. Without a key release is not signed.
val signingProps = Properties().apply {
    val f = rootProject.file("keystore.properties")
    if (f.exists()) f.inputStream().use { load(it) }
}
fun signingValue(prop: String, env: String): String? = signingProps.getProperty(prop) ?: System.getenv(env)
val keystorePath = signingValue("storeFile", "SOFABIRD_KEYSTORE_FILE")

android {
    namespace = "app.sofabird"
    compileSdk = 35
    buildToolsVersion = "35.0.0"

    defaultConfig {
        applicationId = "app.sofabird"
        minSdk = 26
        targetSdk = 35
        versionCode = appVersionCode
        versionName = appVersionName
        vectorDrawables.useSupportLibrary = true
    }

    signingConfigs {
        if (keystorePath != null) {
            create("shared") {
                storeFile = file(keystorePath)
                storePassword = signingValue("storePassword", "SOFABIRD_KEYSTORE_PASSWORD")
                keyAlias = signingValue("keyAlias", "SOFABIRD_KEY_ALIAS")
                keyPassword = signingValue("keyPassword", "SOFABIRD_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        debug {
            // The debug build installs side by side with the test/release one (different applicationId and key).
            applicationIdSuffix = ".debug"
            versionNameSuffix = "-dev"
            manifestPlaceholders["appLabel"] = "Sofabird (dev)"
        }
        release {
            signingConfig = signingConfigs.findByName("shared")
            manifestPlaceholders["appLabel"] = "@string/app_name"
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    packaging {
        resources {
            excludes += setOf(
                "META-INF/INDEX.LIST",
                "META-INF/io.netty.versions.properties",
                "META-INF/{AL2.0,LGPL2.1}",
            )
        }
    }

    testOptions {
        unitTests.isReturnDefaultValues = true
        // Robolectric (screenshot tests) needs the merged resources.
        unitTests.isIncludeAndroidResources = true
    }
}

// Layout screenshot tests: `recordRoborazziDebug` updates the reference images, `verifyRoborazziDebug` compares.
roborazzi {
    outputDir.set(file("src/test/screenshots"))
}

// Unit tests run on the debug variant only: the screenshot tests need the Compose test activity
// (ui-test-manifest), which must not get into the release APK; the other tests do not depend on the variant.
androidComponents {
    beforeVariants(selector().withBuildType("release")) {
        (it as com.android.build.api.variant.HasUnitTestBuilder).enableUnitTest = false
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
        freeCompilerArgs.addAll(
            "-opt-in=kotlinx.serialization.ExperimentalSerializationApi",
            "-opt-in=androidx.compose.material3.ExperimentalMaterial3Api",
            "-opt-in=androidx.compose.foundation.ExperimentalFoundationApi",
            "-opt-in=kotlinx.coroutines.ExperimentalCoroutinesApi",
            "-opt-in=kotlinx.coroutines.FlowPreview",
        )
    }
}

dependencies {
    implementation(project(":core:protocol"))

    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.kotlinx.serialization.json)

    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.bundles.lifecycle)
    implementation(libs.androidx.navigation.compose)
    implementation(libs.androidx.datastore.preferences)
    implementation(libs.androidx.security.crypto)
    implementation(libs.androidx.media3.session)
    implementation(libs.androidx.media3.common)

    implementation(platform(libs.compose.bom))
    implementation(libs.bundles.compose)
    debugImplementation(libs.compose.ui.tooling)

    implementation(libs.bundles.ktor)
    implementation(libs.okhttp)

    implementation(libs.koin.android)
    implementation(libs.koin.androidx.compose)

    implementation(libs.coil.compose)
    implementation(libs.coil.network.okhttp)

    testImplementation(libs.junit)
    testImplementation(libs.kotlinx.coroutines.test)
    testImplementation(libs.ktor.client.mock)
    testImplementation(platform(libs.compose.bom))
    testImplementation(libs.compose.ui.test.junit4)
    testImplementation(libs.robolectric)
    testImplementation(libs.roborazzi)
    testImplementation(libs.roborazzi.compose)
    testImplementation(libs.roborazzi.junit.rule)
    debugImplementation(libs.compose.ui.test.manifest)
}
