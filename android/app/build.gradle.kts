plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// Release signing: values come from ~/.gradle/gradle.properties or environment variables
// (PHONEBOX_STORE_FILE, PHONEBOX_STORE_PASSWORD, PHONEBOX_KEY_ALIAS, PHONEBOX_KEY_PASSWORD).
// Without them, release builds fall back to the debug key so the project still builds anywhere.
fun signingValue(name: String): String? =
    (project.findProperty(name) as String?) ?: System.getenv(name)

android {
    namespace = "net.oxog.phonebox.deskpanel"
    compileSdk = 35

    defaultConfig {
        applicationId = "net.oxog.phonebox.deskpanel"
        minSdk = 21 // Android 5.0; see CONTRIBUTING.md before changing it
        targetSdk = 35
        versionCode = 46
        versionName = "0.9.25"
    }

    signingConfigs {
        val storeFile = signingValue("PHONEBOX_STORE_FILE")
        if (storeFile != null && file(storeFile).exists()) {
            create("release") {
                this.storeFile = file(storeFile)
                storePassword = signingValue("PHONEBOX_STORE_PASSWORD")
                keyAlias = signingValue("PHONEBOX_KEY_ALIAS")
                keyPassword = signingValue("PHONEBOX_KEY_PASSWORD")
                // Sign v3 as well as v1+v2: an APK that lacks v3 is refused as "signatures do not match"
                // when the installed app was signed with v3, which is what the debug key produces.
                enableV1Signing = true
                enableV2Signing = true
                enableV3Signing = true
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.findByName("release") ?: signingConfigs.getByName("debug")
        }
    }

    buildFeatures {
        buildConfig = true
    }

    lint {
        // Unsupported framework calls can crash old phones before the panel opens.
        fatal += "NewApi"
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
}
