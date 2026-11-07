import java.io.File
import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// The phone UI lives in ../web; xterm.js and the terminal colours come from the desktop app, so both look the same.
val webRoot: File = rootProject.file("web")
val desktopRoot: File = File(rootProject.projectDir.parentFile, "src/Maid/wwwroot")

// Release signing: android/keystore/keystore.properties (storeFile, storePassword, keyAlias, keyPassword); not in git.
// An update installs over the app only when it is signed with the same key: keep that folder.
val keystoreDir: File = rootProject.file("keystore")
val keystoreProps = Properties().apply {
    val f = File(keystoreDir, "keystore.properties")
    if (f.isFile) f.inputStream().use { load(it) }
}
val hasReleaseKey = keystoreProps.getProperty("storeFile") != null

// The one app on the phone that may hand voice requests to Maid and use its API through JarvisRelay: its package and the
// SHA-256 of its signing certificate (hex). Give both as Gradle properties (-P…, or ~/.gradle/gradle.properties);
// without them the relay lets no app in.
val voiceAppPackage: String = (findProperty("maid.voiceApp.package") as String?)?.trim().orEmpty()
val voiceAppCertificate: String = (findProperty("maid.voiceApp.certificate") as String?)?.trim()?.replace(":", "")?.lowercase().orEmpty()

android {
    namespace = "com.xpnemec.maid"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.xpnemec.maid"
        // Android 10: BiometricPrompt with the screen lock as a fallback
        minSdk = 29
        targetSdk = 36
        versionCode = 7
        versionName = "1.6"
        buildConfigField("String", "VOICE_APP_PACKAGE", "\"$voiceAppPackage\"")
        buildConfigField("String", "VOICE_APP_CERTIFICATE", "\"$voiceAppCertificate\"")
        // <queries> needs a package name; this one never exists.
        manifestPlaceholders["voiceAppPackage"] = voiceAppPackage.ifEmpty { "invalid.no.voice.app" }
    }

    signingConfigs {
        if (hasReleaseKey) {
            create("release") {
                storeFile = File(keystoreDir, keystoreProps.getProperty("storeFile"))
                storePassword = keystoreProps.getProperty("storePassword")
                keyAlias = keystoreProps.getProperty("keyAlias")
                keyPassword = keystoreProps.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            signingConfig = if (hasReleaseKey) signingConfigs.getByName("release") else {
                logger.warn("android/keystore/keystore.properties not found: the release APK is signed with the debug key")
                signingConfigs.getByName("debug")
            }
        }
    }

    buildFeatures {
        buildConfig = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    implementation("androidx.webkit:webkit:1.12.1")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    // Google's code scanner: scans the pairing QR code in a screen of Play services, without the camera permission
    implementation("com.google.android.gms:play-services-code-scanner:16.1.0")
    // ModuleInstall: has Play services fetch the scanner ahead of the first scan
    implementation("com.google.android.gms:play-services-base:18.5.0")
}

// --- bundled web UI -------------------------------------------------------------------------------------------
// ../web plus, from the desktop app, xterm.js (lib/) and termcolor.js → src/main/assets/www (generated, gitignored).

abstract class BundleWebTask : DefaultTask() {
    @get:InputDirectory @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val webDir: DirectoryProperty

    @get:InputDirectory @get:PathSensitive(PathSensitivity.RELATIVE)
    abstract val desktopDir: DirectoryProperty

    @get:OutputDirectory
    abstract val outputDir: DirectoryProperty

    @TaskAction
    fun bundle() {
        val out = outputDir.get().asFile
        out.deleteRecursively()
        out.mkdirs()
        val web = webDir.get().asFile
        web.walkTopDown().filter { it.isFile }.forEach { it.copyTo(File(out, it.relativeTo(web).invariantSeparatorsPath), overwrite = true) }
        val desktop = desktopDir.get().asFile
        listOf("termcolor.js", "lib/xterm.js", "lib/xterm.css", "lib/addon-fit.js", "lib/addon-unicode11.js", "lib/XTERM-LICENSE.txt")
            .forEach { File(desktop, it).copyTo(File(out, it), overwrite = true) }
        logger.lifecycle("Bundled the phone UI into $out")
    }
}

val bundleWeb = tasks.register<BundleWebTask>("bundleWeb") {
    group = "build"
    description = "Copies the phone UI (../web) and xterm.js from the desktop app into assets/www"
    webDir.set(webRoot)
    desktopDir.set(desktopRoot)
    outputDir.set(layout.projectDirectory.dir("src/main/assets/www"))
}

tasks.named("preBuild") { dependsOn(bundleWeb) }

// --- release APK -> android/dist/Maid.apk -----------------------------------------------------------------------

val copyReleaseApk = tasks.register<Copy>("copyReleaseApk") {
    from(layout.buildDirectory.dir("outputs/apk/release")) {
        include("*.apk")
        rename { "Maid.apk" }
    }
    into(rootProject.layout.projectDirectory.dir("dist"))
    doLast { logger.lifecycle("APK: " + rootProject.file("dist/Maid.apk")) }
}

tasks.matching { it.name == "assembleRelease" }.configureEach { finalizedBy(copyReleaseApk) }
