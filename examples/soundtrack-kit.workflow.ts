using Valencia.Scripting;

function run(api: ScriptApi): string {
    const sequence = api.SelectedSequence();
    const video = api.FirstTrack(sequence, "video");
    const sound = api.FirstTrack(sequence, "audio");
    const outgoingVideo = api.FirstClipOnTrack(sequence, video);
    const outgoingAudio = api.FirstClipOnTrack(sequence, sound);
    const incomingVideo = api.Argument("nextVideo", "0abf107c-7073-4ab0-980a-45b11ac22fda");
    const incomingAudio = api.Argument("nextAudio", "7e5f8a1e-a0f7-4ca9-97ae-21ce077d4270");
    const music = api.Import(api.Argument("music", "artifacts/local/soundtrack-kit/bed.wav"), "64b00839-cc79-4beb-819b-fa3c004e583d");
    const hit = api.Import(api.Argument("hit", "artifacts/local/soundtrack-kit/hit.wav"), "c9104455-8a31-4556-a986-3977774A5572");
    const musicTrack = "01f6f34a-0d88-4bd1-b682-64af8937f455";
    const hitTrack = "8d2f53ef-be81-4e4b-a81b-38e0f7fb8f17";
    const trackCount = api.TrackCount(sequence);
    const plan = api.Plan(sequence);
    plan.AddTrack(musicTrack, "audio", trackCount, "Music");
    plan.AddTrack(hitTrack, "audio", trackCount + 1, "Sound hits");
    plan.Transition(video, "8dad2905-5078-4508-abd2-acf0225c07e6", outgoingVideo, incomingVideo, api.Frames(8L, 30, 1), api.Frames(8L, 30, 1));
    plan.Transition(sound, "bcdca1c8-eb07-4338-a7d7-0b87588967d4", outgoingAudio, incomingAudio, api.Frames(5L, 30, 1), api.Frames(5L, 30, 1));

    const cueDuration = api.Frames(8L, 30, 1);
    plan.SoundHit(musicTrack, "1bb6028d-d958-43e9-8b44-efeaa548d4f1", music, api.FirstAudioStream(music),
        0L, 0L, api.Frames(360L, 30, 1), 0.0);
    plan.SoundHit(hitTrack, "a3119d37-6b16-4bc3-b3de-db9e6d42576b", hit, api.FirstAudioStream(hit),
        api.Frames(97L, 30, 1), 0L, cueDuration, -3.0);
    plan.SoundHit(hitTrack, "e2c5c744-42af-4156-b60c-dd0f094e6cfb", hit, api.FirstAudioStream(hit),
        api.Frames(150L, 30, 1), 0L, cueDuration, -6.0);
    plan.TrackGain(musicTrack, -9.0);

    const level = plan.GainEnvelope();
    level.Point(0L, -18.0);
    level.Point(api.Frames(22L, 30, 1), 0.0);
    level.Point(api.Frames(93L, 30, 1), 0.0);
    level.Point(api.Frames(97L, 30, 1), -12.0);
    level.Point(api.Frames(112L, 30, 1), 0.0);
    level.Point(api.Frames(146L, 30, 1), 0.0);
    level.Point(api.Frames(150L, 30, 1), -9.0);
    level.Point(api.Frames(166L, 30, 1), 0.0);
    level.Point(api.Frames(326L, 30, 1), 0.0);
    level.Point(api.Frames(360L, 30, 1), -24.0);
    plan.TrackEnvelope(musicTrack, level);
    return api.Apply(plan);
}
