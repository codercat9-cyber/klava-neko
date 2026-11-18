# Чужие модели и библиотеки

Модели в репозиторий не входят — их качают скрипты из `tools\` с официальных страниц; условия — у авторов.
«См. карточку» — лицензия на странице модели не указана явно, прежде чем распространять дальше, смотри источник.

## Модели

| Что | Для чего | Источник | Лицензия |
|---|---|---|---|
| Qwen3.6-35B-A3B Uncensored (HauhauCS) | свой мозг | huggingface.co/HauhauCS/Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive (основа Qwen/Qwen3.6-35B-A3B) | Apache-2.0 |
| Qwen3-TTS 12Hz 0.6B Base, Tokenizer 12Hz, 1.7B VoiceDesign | большой голос | huggingface.co/Qwen | Apache-2.0 |
| Qwen3-TTS 1.7B Base, адаптер ударений LoRA v3 (sknyazev/qwen3-tts-12hz-1.7b-ru-stress-gguf, olluorg/voicy) | большой голос: ударения по знаку | huggingface.co | Apache-2.0 |
| RUAccent (ruaccent/accentuator) | расстановка ударений и ё | huggingface.co/ruaccent | код Apache-2.0, модели MIT |
| mineflayer, mineflayer-pathfinder | Клава в Minecraft | github.com/PrismarineJS | MIT |
| Open-Meteo | погода | open-meteo.com | CC BY 4.0 (данные), без ключа |
| whisper large-v3-turbo, small (ggml) | большой слух | huggingface.co/ggerganov/whisper.cpp | MIT |
| WavLM base plus sv | профили (большой комплект) | huggingface.co/microsoft/wavlm-base-plus-sv | см. карточку (Microsoft) |
| GigaAM v3 CTC с пунктуацией (sherpa-onnx) | лёгкий слух | github.com/salute-developers/GigaAM | MIT |
| Supertonic-3 (sherpa-onnx int8) | лёгкий голос | huggingface.co/Supertone/supertonic-3 | код MIT, модель OpenRAIL-M |
| Piper ru_RU irina medium | запасной лёгкий голос | github.com/rhasspy/piper | Piper — MIT; датасет голоса (RHVoice) — см. карточку |
| Silero VAD | лёгкий слух | github.com/snakers4/silero-vad | MIT |
| ERes2Net SV VoxCeleb (3D-Speaker) | профили (лёгкий комплект) | github.com/modelscope/3D-Speaker | Apache-2.0 |
| Vita.vrm (AvatarSample, pixiv β) | аватар | vroid.pixiv.help | CC0 |

## Программы и библиотеки

| Что | Лицензия |
|---|---|
| llama.cpp, whisper.cpp (ggml-org) | MIT |
| sherpa-onnx (k2-fsa) | Apache-2.0 |
| ONNX Runtime (Microsoft) | MIT |
| NAudio | MIT |
| Microsoft.Web.WebView2 | лицензия Microsoft для WebView2 SDK |
| three.js | MIT |
| @pixiv/three-vrm | MIT |
| faster-qwen3-tts, qwen-tts-hf | см. PyPI / репозитории |
| ruaccent | Apache-2.0 |
| pymorphy3 | MIT |
| PyTorch | BSD-3-Clause |
| mineflayer | MIT |

## Звуки еды и газов (web/voices/sfx)

Записи с freesound.org, только CC0 (общественное достояние, указание автора не требуется — указываем из уважения);
отобраны судьёй AudioSet (tools/sfx_collect.py), разрезаны на отдельные события.

| Файл | Звук | Автор | Страница |
|---|---|---|---|
| burp_00.wav | Burp! | Breviceps | https://freesound.org/people/Breviceps/sounds/444496/ |
| burp_01.wav | burp42.wav | Reitanna | https://freesound.org/people/Reitanna/sounds/323712/ |
| burp_02.wav | burp41.wav | Reitanna | https://freesound.org/people/Reitanna/sounds/323713/ |
| burp_03.wav | The Best Burp Sample On The Internet | scooterfort | https://freesound.org/people/scooterfort/sounds/570266/ |
| burp_04.wav | Burp or Puke.wav | BigKahuna360 | https://freesound.org/people/BigKahuna360/sounds/263508/ |
| burp_05.wav | Burp.wav | jawbutch | https://freesound.org/people/jawbutch/sounds/344406/ |
| burp_06.wav | burp.wav | gmajorrr | https://freesound.org/people/gmajorrr/sounds/132717/ |
| burp_07.wav | A Very Nice Burp!.mp3 | bnabors1 | https://freesound.org/people/bnabors1/sounds/277044/ |
| burp_08.wav | Long burp / Langer Rülps.wav | 1000kcirtap | https://freesound.org/people/1000kcirtap/sounds/323650/ |
| burp_09.wav | belch | AlaskaRobotics | https://freesound.org/people/AlaskaRobotics/sounds/221516/ |
| burp_10.wav | burp.aiff | kyles | https://freesound.org/people/kyles/sounds/51803/ |
| burp_11.wav | belch1.wav | santuccie | https://freesound.org/people/santuccie/sounds/26762/ |
| burp_12.wav | Burp.wav | Pingel | https://freesound.org/people/Pingel/sounds/12730/ |
| burp_13.wav | burps.wav | squashy555 | https://freesound.org/people/squashy555/sounds/250087/ |
| burp_14.wav | burps.wav | squashy555 | https://freesound.org/people/squashy555/sounds/250087/ |
| burp_15.wav | Loud Burp | AquaChannel | https://freesound.org/people/AquaChannel/sounds/214451/ |
| burp_16.wav | Burp - medium-deep | Sadiquecat | https://freesound.org/people/Sadiquecat/sounds/814323/ |
| burp_17.wav | Burp.mp3 | DSPena | https://freesound.org/people/DSPena/sounds/167139/ |
| burp_18.wav | burps.wav | squashy555 | https://freesound.org/people/squashy555/sounds/250087/ |
| burp_19.wav | burp1.wav | dleigh | https://freesound.org/people/dleigh/sounds/346147/ |
| burp_20.wav | Burp Low.wav | baidonovan | https://freesound.org/people/baidonovan/sounds/187333/ |
| burp_21.wav | long Burp/Belch | Badly99 | https://freesound.org/people/Badly99/sounds/180377/ |
| burp_22.wav | burp.wav | etheron369 | https://freesound.org/people/etheron369/sounds/331630/ |
| burp_23.wav | Ed Burp and Pop.wav | Arcadium1 | https://freesound.org/people/Arcadium1/sounds/221851/ |
| fart_00.wav | small-realpoots105-110.wav | Jixolros | https://freesound.org/people/Jixolros/sounds/556505/ |
| fart_01.wav | 17 pet.wav | carlosramon | https://freesound.org/people/carlosramon/sounds/186119/ |
| fart_02.wav | wet fart.mp3 | kuchtaa | https://freesound.org/people/kuchtaa/sounds/555418/ |
| fart_03.wav | HUGE fart.wav | taliasauce | https://freesound.org/people/taliasauce/sounds/158739/ |
| fart_04.wav | Fart.wav | smokevhstapes | https://freesound.org/people/smokevhstapes/sounds/177050/ |
| fart_05.wav | Short, definite fart | ycbcr | https://freesound.org/people/ycbcr/sounds/249583/ |
| fart_06.wav | Fart (real) | shaundoogan | https://freesound.org/people/shaundoogan/sounds/465486/ |
| fart_07.wav | Nasty fart | SamsterBirdies | https://freesound.org/people/SamsterBirdies/sounds/645339/ |
| fart_08.wav | zort2.mp3 | Baturkmez | https://freesound.org/people/Baturkmez/sounds/324451/ |
| fart_09.wav | small fart.wav | KataVlogsYT | https://freesound.org/people/KataVlogsYT/sounds/324453/ |
| fart_10.wav | FART.aif | Manicciola | https://freesound.org/people/Manicciola/sounds/121783/ |
| fart_11.wav | Raspberry.aif | oldedgar | https://freesound.org/people/oldedgar/sounds/97979/ |
| fart_12.wav | small-realpoots105-110.wav | Jixolros | https://freesound.org/people/Jixolros/sounds/556505/ |
| fart_13.wav | Short Fart | M0nsterHD | https://freesound.org/people/M0nsterHD/sounds/814854/ |
| fart_14.wav | fart2.wav | dleigh | https://freesound.org/people/dleigh/sounds/346144/ |
| fart_15.wav | zort2.mp3 | Baturkmez | https://freesound.org/people/Baturkmez/sounds/324451/ |
| fart_16.wav | small-realpoots105-110.wav | Jixolros | https://freesound.org/people/Jixolros/sounds/556505/ |
| fart_17.wav | fart3.wav | dleigh | https://freesound.org/people/dleigh/sounds/346143/ |
| fart_18.wav | fart.mp3 | MacKaffee | https://freesound.org/people/MacKaffee/sounds/326143/ |
| fart_19.wav | fart 7.mp3 | Flash_Shumway | https://freesound.org/people/Flash_Shumway/sounds/114724/ |
| crunch_00.wav | Bite potato chips | sammy214 | https://freesound.org/people/sammy214/sounds/316926/ |
| crunch_01.wav | Eating Chips.wav | ryanharding95 | https://freesound.org/people/ryanharding95/sounds/272440/ |
| crunch_02.wav | potato chips2.wav | anagar | https://freesound.org/people/anagar/sounds/267965/ |
| crunch_03.wav | Eat_Crunchy.mp3 | schoman3 | https://freesound.org/people/schoman3/sounds/507453/ |
| crunch_04.wav | Chomp on Chip.wav | bbrocer | https://freesound.org/people/bbrocer/sounds/382660/ |
| crunch_05.wav | Short crisps eating noise | vibritherabjit123 | https://freesound.org/people/vibritherabjit123/sounds/573577/ |
| crunch_06.wav | eating chips | nettimato | https://freesound.org/people/nettimato/sounds/369265/ |
| crunch_07.wav | Eating Crisps | WasabiWielder | https://freesound.org/people/WasabiWielder/sounds/334209/ |
| crunch_08.wav | Apple Bite | AntumDeluge | https://freesound.org/people/AntumDeluge/sounds/584290/ |
| crunch_09.wav | Crunching Chips.wav | TheDCHeck | https://freesound.org/people/TheDCHeck/sounds/393816/ |
| crunch_10.wav | Eating Chips.wav | bbrocer | https://freesound.org/people/bbrocer/sounds/382664/ |
| crunch_11.wav | Eating Chips.wav | ryanharding95 | https://freesound.org/people/ryanharding95/sounds/272440/ |
| chew_00.wav | Chewing Crunch Cornflakes Knuspern Schnurpsen Eating Essen Munch | Borgory | https://freesound.org/people/Borgory/sounds/548367/ |
| chew_01.wav | Chomp Chew Bite.wav | bbrocer | https://freesound.org/people/bbrocer/sounds/382650/ |

## Добавлено 29.09

| Что | Для чего | Источник | Лицензия |
|---|---|---|---|
| Dialogs (langswap, Interspeech 2026) — женские голоса Маша и Света | обучение эмоциональной LoRA голоса (веса адаптера в репозиторий не входят) | huggingface.co/datasets/langswap/dialogs-ru-emotional-conversations | OpenRAIL |
| RESD (Aniemore) | обучение эмоциональной LoRA | huggingface.co/datasets/Aniemore/resd | см. карточку |
| bge-m3 Q8_0 GGUF (BAAI, ggml-org) | векторы графа памяти (MemoryGraph) | huggingface.co/ggml-org/bge-m3-Q8_0-GGUF | MIT |
| AST AudioSet (MIT/ast-finetuned-audioset-10-10-0.4593) | судья звуков при отборе (только проверка) | huggingface.co/MIT/ast-finetuned-audioset-10-10-0.4593 | BSD-3-Clause |
| HuBERT-large SER Dusha (xbgoose) | судья эмоций голоса (только проверка) | huggingface.co/xbgoose/hubert-large-speech-emotion-recognition-russian-dusha-finetuned | см. карточку |

## Добавлено 30.09 — музыкальный слух и пение (music\, KlavaDesk: Music*.cs)

| Что | Для чего | Источник | Лицензия |
|---|---|---|---|
| Demucs htdemucs_ft (Meta) | трек → голос, ударные, бас, остальное | github.com/facebookresearch/demucs | MIT (код и веса) |
| beat_this (CPJKU, ISMIR 2024), чекпоинт final0 | доли и сильные доли | github.com/CPJKU/beat_this | MIT |
| Basic Pitch (Spotify), ICASSP 2022 ONNX | ноты баса и инструментов (полифония) | github.com/spotify/basic-pitch | Apache-2.0 |
| RMVPE (из RVC, lj1995/VoiceConversionWebUI rmvpe.pt) | тон голоса по 10 мс → мелодия | huggingface.co/lj1995/VoiceConversionWebUI | MIT |
| AST AudioSet (MIT/ast-finetuned-audioset-10-10-0.4593) | инструменты, жанр, настроение, тип голоса | huggingface.co/MIT/ast-finetuned-audioset-10-10-0.4593 | BSD-3-Clause |
| wav2vec2 xlsr-53 espeak cv-ft (Meta) | фонемы: русские слоги своей речи → ноты | huggingface.co/facebook/wav2vec2-xlsr-53-espeak-cv-ft | Apache-2.0 |
| ACE-Step 1.5 (ACE Studio × StepFun): DiT turbo, 5Hz LM 1.7B, VAE; Qwen3-Embedding-0.6B (Alibaba) | русский кавер живым вокалом по смысловым кодам оригинала (music\ace_gen.py, своё окружение tools\ace-step) | github.com/ACE-Step/ACE-Step-1.5, huggingface.co/ACE-Step/Ace-Step1.5 | MIT; Qwen3-Embedding — Apache-2.0 |
| Seed-VC (Plachta), DiT whisper-base f0 44k + BigVGAN v2 44k (NVIDIA) | её тембр поверх направляющего голоса (пение) | github.com/Plachtaa/seed-vc, huggingface.co/Plachta/Seed-VC | GPL-3.0 (код в tools\seed-vc со своей лицензией); BigVGAN — MIT |
| WORLD / pyworld | растяжка гласных и тон по нотам | github.com/JeremyCCHsu/Python-Wrapper-for-World-Vocoder | MIT (WORLD — modified BSD) |
| pretty_midi, mido, mir_eval, resampy, julius, dora-search, openunmix, rotary-embedding-torch | MIDI и зависимости разбора | PyPI | MIT / ISC / BSD |
| MeltySynth (sinshu) | «похожая основа»: MIDI → звук на C# | github.com/sinshu/meltysynth | MIT |
| GeneralUser GS v2.0 (S. Christian Collins) | звуковой банк инструментов (SoundFont) | github.com/mrbumpy409/GeneralUser-GS | GeneralUser GS License v2.0 (свободное использование, в т.ч. в программах) |

Треки, которые друг даёт слушать и петь, их стемы и каверы лежат в `data\music` — в репозиторий не входят.
