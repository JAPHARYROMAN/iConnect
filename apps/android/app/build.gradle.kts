import com.google.protobuf.gradle.*
import groovy.json.JsonOutput
import org.gradle.api.artifacts.component.ModuleComponentIdentifier
import java.security.MessageDigest
import java.util.HexFormat

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("com.google.protobuf")
}
android {
    namespace = "com.iconnect.proof"
    compileSdk = 36
    defaultConfig {
        applicationId = "com.iconnect.proof"
        minSdk = 29
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0-proof"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }
    buildFeatures { compose = true; buildConfig = true }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    sourceSets["main"].proto { srcDir(rootProject.file("../../shared/protocol")) }
    sourceSets["test"].resources.srcDir(rootProject.file("../../shared/test-vectors"))
    sourceSets["androidTest"].assets.srcDir(rootProject.file("../../shared/test-vectors"))
    buildTypes { release { isMinifyEnabled = false } }
}
androidComponents.onVariants(androidComponents.selector().withBuildType("release")) { variant ->
    val releaseCode = providers.gradleProperty("iconnectReleaseVersionCode")
    val releaseName = providers.gradleProperty("iconnectReleaseVersionName")
    require(releaseCode.isPresent == releaseName.isPresent) { "Provide both Android release version properties." }
    if (releaseCode.isPresent) {
        val code = releaseCode.get().toIntOrNull()
        require(code != null && code in 1..2_100_000_000) { "Invalid Android release version code." }
        val name = releaseName.get()
        require(name.length <= 64 && name.matches(Regex("[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?"))) {
            "Invalid Android release version name."
        }
        variant.outputs.forEach { output ->
            output.versionCode.set(code)
            output.versionName.set(name)
        }
    }
    val output = variant.outputs.single()
    val releaseArtifacts = configurations.named(variant.runtimeConfiguration.name)
        .flatMap { it.incoming.artifacts.resolvedArtifacts }
        .map { resolved -> resolved.sortedBy { it.id.displayName } }
    val sbom = tasks.register<WriteAndroidReleaseSbom>("writeReleaseSbom") {
        applicationId.set(variant.applicationId)
        appVersion.set(output.versionName)
        versionCode.set(output.versionCode)
        coordinates.set(releaseArtifacts.map { resolved ->
            resolved.map { artifact ->
                val module = artifact.id.componentIdentifier as? ModuleComponentIdentifier
                    ?: error("Release SBOM requires a Maven identity for ${artifact.id.displayName}.")
                "${module.group}:${module.module}:${module.version}"
            }
        })
        artifactFiles.set(releaseArtifacts.map { resolved -> resolved.map { it.file } })
        outputDirectory.set(layout.buildDirectory.dir("generated/release-sbom"))
    }
    requireNotNull(variant.sources.assets).addGeneratedSourceDirectory(sbom, WriteAndroidReleaseSbom::outputDirectory)
}
kotlin { compilerOptions { jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17) } }
protobuf {
    protoc { artifact = "com.google.protobuf:protoc:3.25.8" }
    generateProtoTasks { all().configureEach { builtins { maybeCreate("java").option("lite") } } }
}
dependencies {
    // This BOM supports the project's API 36 / AGP 8.13 toolchain.
    implementation(platform("androidx.compose:compose-bom:2025.10.01"))
    implementation("androidx.activity:activity-compose:1.12.0")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.9.4")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.9.4")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
    implementation("com.google.protobuf:protobuf-javalite:3.25.8")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("io.github.webrtc-sdk:android:150.7871.04")
    implementation("com.journeyapps:zxing-android-embedded:4.3.0")
    testImplementation("junit:junit:4.13.2")
    androidTestImplementation("androidx.test.ext:junit:1.3.0")
    androidTestImplementation("androidx.test:runner:1.7.0")
}

abstract class WriteAndroidReleaseSbom : DefaultTask() {
    @get:Input abstract val applicationId: Property<String>
    @get:Input abstract val appVersion: Property<String>
    @get:Input abstract val versionCode: Property<Int>
    @get:Input abstract val coordinates: ListProperty<String>
    @get:InputFiles abstract val artifactFiles: ListProperty<File>
    @get:OutputDirectory abstract val outputDirectory: DirectoryProperty

    @TaskAction
    fun writeBom() {
        val coordinatePart = Regex("[A-Za-z0-9_.-]+")
        val resolved = coordinates.get()
        val files = artifactFiles.get()
        check(resolved.size == files.size && resolved.isNotEmpty()) { "Release runtime artifacts were not resolved." }
        val components = resolved.zip(files).groupBy({ it.first }, { it.second }).toSortedMap().map { (coordinate, artifacts) ->
            val parts = coordinate.split(':')
            require(parts.size == 3 && parts.all(coordinatePart::matches)) {
                "Release SBOM requires a canonical Maven coordinate."
            }
            val (group, name, version) = parts
            val purl = "pkg:maven/$group/$name@$version"
            mapOf("bom-ref" to purl, "type" to "library", "group" to group, "name" to name, "version" to version, "purl" to purl,
                "hashes" to artifacts.map(::sha256).distinct().sorted().map { mapOf("alg" to "SHA-256", "content" to it) })
        }
        val bom = mapOf("\$schema" to "http://cyclonedx.org/schema/bom-1.6.schema.json", "bomFormat" to "CycloneDX",
            "specVersion" to "1.6", "version" to 1, "metadata" to mapOf("component" to mapOf(
                "type" to "application", "name" to applicationId.get(), "version" to appVersion.get(),
                "properties" to listOf(mapOf("name" to "iconnect:android:versionCode", "value" to versionCode.get().toString())))),
            "components" to components)
        val file = outputDirectory.file("iconnect/sbom.cdx.json").get().asFile
        file.parentFile.mkdirs()
        file.writeText(JsonOutput.prettyPrint(JsonOutput.toJson(bom)) + "\n", Charsets.UTF_8)
    }

    private fun sha256(file: File): String {
        val digest = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { input ->
            val bytes = ByteArray(64 * 1024)
            while (true) {
                val count = input.read(bytes)
                if (count < 0) break
                digest.update(bytes, 0, count)
            }
        }
        return HexFormat.of().formatHex(digest.digest())
    }
}
