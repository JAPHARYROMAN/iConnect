package com.iconnect.proof

enum class PhoneIncomingFinalizationStage(val label: String, internal val measured: Boolean = false) {
    PREPARING("Preparing the saved copy for verification"),
    CHECKING_PRIVATE_BYTES("Verifying the saved bytes", true),
    PREPARING_PROVIDER_DOCUMENT("Preparing the new provider document"),
    WRITING_PROVIDER_DOCUMENT("Copying bytes into the provider document", true),
    SYNCING_PROVIDER_DOCUMENT("Flushing and syncing the provider document"),
    CHECKING_PROVIDER_BYTES("Rereading the provider document", true)
}

data class PhoneIncomingFinalization(
    val stage: PhoneIncomingFinalizationStage,
    val size: Long,
    val offset: Long,
    val estimate: PhonePayloadRateEstimate?
) {
    fun describe() = if (estimate == null) "${stage.label}. Time remaining is unavailable."
        else "${stage.label} · $offset / $size bytes. ${estimate.describeStage()}"
}

/** One local byte pass at a time, owned by the verification job. */
internal class PhoneIncomingFinalizationTracker(private val id: String, private val size: Long, private val now: () -> Long) {
    private val gate = Any()
    private val rates = PhonePayloadRateTracker()
    private var stage = PhoneIncomingFinalizationStage.PREPARING
    private var offset = 0L
    private var started = false

    fun begin(next: PhoneIncomingFinalizationStage) = synchronized(gate) {
        stage = next
        offset = 0
        started = true
        rates.clear()
        if (stage.measured) rates.observe(id, stage.name, size, offset, true, now())
    }

    fun advance(processed: Long) = synchronized(gate) {
        offset = processed
        if (stage.measured) rates.observe(id, stage.name, size, offset, true, now())
    }

    fun end() = synchronized(gate) {
        started = false
        rates.clear()
    }

    fun read(): PhoneIncomingFinalization = synchronized(gate) { snapshot() }
    fun readStarted(): PhoneIncomingFinalization? = synchronized(gate) { if (started) snapshot() else null }

    private fun snapshot() =
        PhoneIncomingFinalization(stage, size, offset, if (stage.measured) rates.read(id, stage.name, size, offset, true, now()) else null)
}
