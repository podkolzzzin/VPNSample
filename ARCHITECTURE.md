# Архітектура VPNSample

Цей документ описує поточну реалізацію VPN: автоматизацію DigitalOcean, межі між ОС і протоколом, запуск клієнта та шлях IPv4/IPv6-пакетів.

## Загальна схема

```mermaid
flowchart LR
    subgraph Laptop[Mesh-клієнт A]
        App[Firefox та інші програми]
        Route[Linux routing]
        CTun[svpn0<br/>MTU 1280]
        Resolver[systemd-resolved<br/>.vpn → 10.8.0.1]
        Mesh[MeshPacketEndpoint<br/>peer route lookup]
        Direct[Secure UDP<br/>ECDH + AES-GCM]
        Relay[TunnelPipeline<br/>WSS fallback]
        Control[WSS coordination]

        App <--> Route
        App --> Resolver
        Resolver --> Route
        Route <--> CTun
        CTun <--> Mesh
        Mesh <-->|peer packets| Direct
        Mesh <-->|fallback / exit / DNS| Relay
    end

    Direct <-->|encrypted UDP/443<br/>hole punched| Peer[Mesh-клієнт B]
    Relay <-->|TLS WebSocket<br/>TCP/443| STcp
    Control <-->|peer map + keys + candidates| STcp

    subgraph Droplet[DigitalOcean Ubuntu droplet]
        STcp[Kestrel<br/>coordination + relay + cover]
        Rendezvous[UDP rendezvous<br/>server-reflexive endpoints]
        SProtocol[TunnelPipeline per connection<br/>websocket-cover profile]
        Router[TunnelPacketRouter<br/>relay fallback]
        SLinux[LinuxTunDevice]
        STun[one svpn0<br/>10.8.0.1/24 / fd42:8::1/64]
        Forward[Linux IP forwarding]
        Nat[NAT44 / NAT66]
        Dns[VpnSample.Dns<br/>authoritative .vpn]

        STcp <-->|кадри тунелю| SProtocol
        STcp --> Rendezvous
        SProtocol <-->|RoutedPacketEndpoint| Router
        Router <-->|exit-node packets| SLinux
        SLinux <--> STun
        STun <--> Dns
        STun <--> Forward
        Forward <--> Nat
    end

    Direct -.->|binding + probes| Rendezvous

    Nat <--> Internet[Інтернет IPv4 та IPv6]
```

Кожен клієнт має два WSS-з'єднання — data/relay і coordination — та один
стабільний UDP socket. Сервер має лише один TUN `svpn0`.
Після WebSocket Upgrade клієнт надсилає своє DNS-ім'я. Сервер атомарно резервує
ім'я та передає номер `N` у registration response, а клієнт отримує host
`N + 2` у спільних мережах `10.8.0.0/24` і `fd42:8::/64`. Coordination передає
public keys, local candidates і server-reflexive endpoints. Клієнти одночасно
надсилають authenticated UDP probes; після успіху `MeshPacketEndpoint` направляє
peer packets напряму. Internet, DNS та peer traffic без живого direct path йдуть
через WSS relay до `TunnelPacketRouter`.
Тимчасова автоматизація підключається до IP droplet напряму, передає
`vpn.twocubes.io` як WebSocket URI та SNI/Host і перевіряє exact certificate pin.
Kestrel віддає звичайну HTML-сторінку на `/`, а приховані WSS endpoints без
коректних bearer/session credentials повертають `404`. Для постійного
deployment DNS `vpn.twocubes.io` має вказувати на сервер, а сертифікат має бути
виданий довіреним CA.

## Розділення рівнів

```mermaid
flowchart TB
    Client[VpnSample.Client<br/>composition root]
    Server[VpnSample.Server<br/>composition root]
    Linux[VpnSample.Os.Linux<br/>LinuxTunDevice]
    Dns[VpnSample.Dns<br/>registry + DNS wire protocol]
    Mesh[VpnSample.Mesh<br/>coordination + secure UDP + path selection]
    Protocol[VpnSample.Protocol<br/>pipeline + stages + wire codec]
    Kernel[Linux kernel<br/>/dev/net/tun, ip, routes]

    Client --> Protocol
    Client --> Dns
    Client --> Mesh
    Client --> Linux
    Server --> Protocol
    Server --> Dns
    Server --> Mesh
    Server --> Linux
    Linux --> Protocol
    Linux --> Kernel
```

Відповідальність розділена так:

| Рівень | Відповідальність | Не знає про |
|---|---|---|
| `VpnSample.Protocol` | Межа `IPacketEndpoint`, pipeline стадій, wire codec, handshake, packet router і таблиця overlay IP → connection | `/dev/net/tun`, системні маршрути, DigitalOcean |
| `VpnSample.Dns` | Валідація імен, registration handshake, lease registry та authoritative A/AAAA DNS для `.vpn` | TUN, TLS, DigitalOcean |
| `VpnSample.Mesh` | Persistent P-256 identity, peer maps, candidates, UDP rendezvous, AEAD datagrams, replay window, path selection і WSS fallback boundary | Linux routes, Kestrel, DigitalOcean |
| `VpnSample.Os.Linux` | Створення TUN, адреси інтерфейсу, незалежні потоки читання/запису | TCP, сервер, формат розгортання |
| `VpnSample.Client` | WSS-підключення, композиція протоколу з Linux endpoint | Налаштування cloud-сервера |
| `VpnSample.Server` | HTTPS cover site, WSS endpoint, один exit-node TUN і композиція router/pipeline | Клієнтські default routes |
| `scripts/` | Життєвий цикл droplet, deployment, системні маршрути та перевірки | Внутрішній фреймінг пакетів |

Таким чином, OS-level VPN відділений від protocol-level VPN на межі `IPacketEndpoint`. Протокол працює зі `Stream` і не викликає Linux API напряму.

## Pipeline протоколу

`TunnelPipeline` тепер є relay pipeline. На клієнті його `IPacketEndpoint` —
`MeshPacketEndpoint`: direct peer packets він забирає у UDP, решту віддає pipeline.
На сервері endpoint — channel-backed `RoutedPacketEndpoint`. Outbound-стадії
виконуються в порядку реєстрації, а inbound-стадії — у зворотному порядку. Це
дозволяє кодувати кадр на клієнті й симетрично декодувати його на сервері.
`baseline` містить tracing і pass-through. `shuffle-split` додає
перестановку вікон до трьох IP-пакетів із flush через 5 ms, а потім ділить
кожен пакет на tunnel fragments розміром до 256 байтів. Стандартний
`websocket-cover` використовує фрагменти до 240 байтів і `PaddingStage`, який
доповнює їх випадковими байтами до одного з фіксованих size buckets.

```mermaid
flowchart LR
    TunRead[TUN PacketReader] --> Frame[TunnelFrame]
    Frame --> TraceOut[PacketTraceStage]
    TraceOut --> Shuffle[PacketShuffleStage]
    Shuffle --> Fragment[FragmentStage]
    Fragment --> Pad[PaddingStage]
    Pad --> Codec[LengthPrefixedCodec]
    Codec --> TcpWrite[WebSocketDuplexStream]

    TcpRead[WebSocketDuplexStream] --> Decode[LengthPrefixedCodec]
    Decode --> Unpad[PaddingStage]
    Unpad --> Reassemble[FragmentStage reassembly]
    Reassemble --> TraceIn[PacketTraceStage]
    TraceIn --> TunWrite[TUN PacketWriter]
```

Перед обміном кадрами обидві сторони надсилають handshake з magic `SVPN`,
версією протоколу та назвою профілю. Різні версії або профілі завершують сесію
явною помилкою замість пошкодження потоку.

## Формат даних у тунелі

`LengthPrefixedCodec` записує один `TunnelFrame` у такому форматі:

```text
+---------------+--------------+----------------+----------------+------------------+
| uint32 BE     | uint64 BE    | uint16 BE      | uint16 BE      | payload          |
| body length   | packet ID    | fragment index | fragment count | 1..65535 bytes   |
+---------------+--------------+----------------+----------------+------------------+
```

У `baseline` кожен пакет має `fragmentIndex = 0` і `fragmentCount = 1`. У
`shuffle-split` і `websocket-cover` створюють кілька кадрів зі спільним
`packet ID`; на приймальній стороні `FragmentStage` перевіряє індекси та
загальний розмір перед reassembly. У `websocket-cover` поле payload додатково
містить original-length prefix і випадковий padding до bucket size.

## Послідовність розгортання і запуску

```mermaid
sequenceDiagram
    actor User as Оператор
    participant Create as create-droplet.sh
    participant DO as DigitalOcean API
    participant Deploy as deploy-server.sh
    participant Host as Droplet/systemd
    participant Run as run-vpn.sh
    participant Client as VpnSample.Client

    User->>Create: Запустити створення
    Create->>Create: Створити passwordless Ed25519 key
    Create->>DO: Зареєструвати key і створити IPv6-enabled droplet
    DO-->>Create: Droplet ID та публічні адреси
    Create->>Create: Записати .vpn-droplet.env

    User->>Deploy: Розгорнути сервер
    Deploy->>Host: Publish, copy, install .NET та service
    Deploy->>Host: Увімкнути IPv4/IPv6 forwarding
    Deploy->>Host: Додати NAT44 і NAT66
    Deploy->>Deploy: Згенерувати bearer token
    Deploy->>Host: Перевірити cover page і probe → 404

    User->>Run: Запустити локальний VPN
    Run->>Run: Зберегти наявні routes
    Run->>Run: Додати прямий route до VPN server
    Run->>Client: Запустити з sudo
    Client->>Host: TCP connect
    Client->>Host: TLS 1.2/1.3, SNI vpn.twocubes.io, ALPN http/1.1
    Client->>Host: GET /api/v1/events + WebSocket Upgrade + bearer token
    Host-->>Client: 101 Switching Protocols
    Host->>Host: Створити один exit-node svpn0 під час старту
    Client->>Host: Зареєструвати node-name
    Host-->>Client: Registration accepted + номер клієнта N
    Host-->>Client: Одноразовий mesh session token
    Host->>Host: Додати node-name.vpn → IPv4/IPv6
    Host->>Host: Зареєструвати IP клієнта у TunnelPacketRouter
    Client->>Client: Створити svpn0 з host N+2 у спільному subnet
    Client->>Host: WSS /api/v1/mesh + public key + local candidates
    Client->>Host: UDP binding з того самого socket
    Host-->>Client: Peer map + server-reflexive endpoints
    Client->>Client: Authenticated probes + NAT hole punching
    Client->>Client: Вибрати direct UDP path; WSS лишити fallback
    Client->>Host: Handshake version + websocket-cover profile
    Host-->>Client: Handshake version + websocket-cover profile
    Client->>Host: Запустити tunnel pumps
    Run->>Run: Направити зону .vpn на 10.8.0.1 через svpn0
    Run->>Run: Перевірити IPv4 та IPv6 peers
    Run->>Run: Додати fwmark policy route для mesh UDP через WAN
    Run->>Run: Перемкнути IPv4 default route
    Run->>Run: Додати IPv6 default route metric 50
    Run->>Run: Перевірити route selection для IPv4 та IPv6
```

Прямий маршрут до публічної IPv4-адреси droplet залишається через звичайний
gateway. Це не дає TCP-транспорту спробувати пройти через власний тунель.
Окремо `run-vpn.sh` ставить `SO_MARK` на єдиний mesh UDP socket. Незамаркований
IPv4 отримує VPN default в окремій policy table, а marked UDP оминає її і
користується незміненими main routes. Тому UDP-пакети rendezvous, probes,
keepalives і direct data не повертаються рекурсивно в overlay та водночас можуть
використовувати локальні LAN routes. Cleanup видаляє правила й окрему таблицю.

## Private DNS

`VpnSample.Dns` — окрема OS-neutral збірка. `OverlayDnsRegistry` утримує lease
імені лише поки відповідний WebSocket підключений. `OverlayDnsServer` слухає UDP
53 на серверній overlay-адресі `10.8.0.1` і авторитетно відповідає A та AAAA для
`<node>.vpn`. Однакове ім'я не може бути зареєстроване двічі; після disconnect
lease звільняється.

DNS-запит проходить через той самий тунель: `systemd-resolved` відправляє `.vpn`
на `10.8.0.1`, серверний kernel передає пакет UDP socket DNS-сервера, а відповідь
повертається клієнту через server TUN і `TunnelPacketRouter`. Інші DNS-зони не
маршрутизуються до приватного resolver link.

## Шлях пакета

Для вихідного трафіку Firefox послідовність однакова для IPv4 й IPv6:

```mermaid
flowchart LR
    Browser[Firefox] --> CRoute[Маршрути клієнта]
    CRoute --> ClientTun[svpn0]
    ClientTun --> Shuffle[Shuffle packet window]
    Shuffle --> Frame[Split and pad TunnelFrames]
    Frame --> HTTPS[TLS + WebSocket<br/>TCP 443]
    HTTPS --> Unframe[Відновлений IP packet]
    Unframe --> Router[TunnelPacketRouter]
    Router --> ServerTun[shared svpn0]
    ServerTun --> Forward[IP forwarding]
    Forward --> Choice{Версія IP}
    Choice -->|IPv4| NAT44[iptables MASQUERADE]
    Choice -->|IPv6| NAT66[ip6tables MASQUERADE]
    NAT44 --> Web[Цільовий сайт]
    NAT66 --> Web
```

Відповідь проходить той самий шлях у зворотному напрямку. Тому сайт має бачити публічні IPv4 та IPv6 droplet, а не адреси локального провайдера.

Для client-to-client пакета клієнтський route table знаходить peer за overlay IP.
Якщо є authenticated path, пакет шифрується pairwise AES-256-GCM key і йде
напряму. Якщо probes не пройшли або path не отримував відповідей 20 секунд,
той самий пакет автоматично потрапляє у WSS relay:

```mermaid
flowchart LR
    A[Client A TUN<br/>10.8.0.2] --> Lookup{Live direct path?}
    Lookup -->|yes| AEAD[Encrypt + sequence + replay metadata]
    AEAD --> UDP[Hole-punched UDP]
    UDP --> B[Client B TUN<br/>10.8.0.3]
    Lookup -->|no| WSS[WSS relay]
    WSS --> Router[TunnelPacketRouter]
    Router --> B
```

## Адреси та маршрути

| Призначення | Клієнт | Сервер |
|---|---|---|
| Tunnel IPv4 | `10.8.0.X/24`, connected route `10.8.0.0/24` | `10.8.0.1/24` на одному `svpn0` |
| Tunnel IPv6 | `fd42:8::X/64`, connected route `fd42:8::/64` | `fd42:8::1/64` на одному `svpn0` |
| Default IPv4 | Перемикається на `svpn0` | Forward + NAT44 у зовнішню мережу |
| Default IPv6 | Додається через `svpn0` з metric `50` | Forward + NAT66 у зовнішню мережу |
| VPN transport | Прямий route до public IPv4 droplet, WSS із SNI `vpn.twocubes.io` | Kestrel HTTPS/WSS на `0.0.0.0:443` |
| Mesh data | Pairwise encrypted UDP, local/reflexive candidates, fallback WSS | UDP rendezvous на `0.0.0.0:443` + WSS relay |
| Private DNS | `systemd-resolved`: зона `.vpn` через `svpn0` | UDP `10.8.0.1:53`, authoritative A/AAAA |

Metric IPv6-маршруту можна змінити через `VPN_ROUTE_METRIC`. Скрипт перевіряє фактичний результат `ip route get` для обох сімейств адрес і завершується з помилкою, якщо маршрут оминає `svpn0`.

## Зупинка та відновлення мережі

```mermaid
sequenceDiagram
    actor User as Оператор
    participant Run as run-vpn.sh
    participant Client as VpnSample.Client
    participant Protocol as TunnelPipeline
    participant Tun as LinuxTunDevice

    User->>Run: Ctrl+C або завершення
    Run->>Run: Відновити DNS-конфігурацію svpn0
    Run->>Client: SIGTERM
    Client->>Protocol: Cancellation
    Protocol->>Tun: InterruptReadAsync
    Protocol->>Protocol: Дочекатися обох pumps
    Client->>Tun: Dispose і видалення svpn0
    Run->>Run: Відновити IPv4 default routes
    Run->>Run: Видалити точний IPv6 VPN default route
    Run->>Run: Відновити route до VPN server
```

Окремо `create-droplet.sh --delete` видаляє droplet, зареєстрований у DigitalOcean SSH key, локальну тимчасову пару ключів і state-файл.

## Поточні обмеження

- Сервер має 253 адреси клієнтів і повторно використовує адресу після відключення.
- DNS працює лише через UDP, підтримує A/AAAA/ANY і тримає записи лише в пам'яті.
- Вузли мають persistent P-256 identities і pairwise keys, але ще немає enrollment,
  ACL, key revocation або автоматичної ротації.
- Direct data path зараз використовує лише IPv4 underlay candidates і спрощений
  ICE-like rendezvous, а не повні STUN/ICE/PCP/NAT-PMP.
- WSS fallback не є окремою географічною DERP мережею; при недоступності одного
  coordination/relay сервера нові сесії не встановляться. Relay також завершує
  TLS на сервері й не є blind end-to-end encrypted relay.
- На fallback можливий ефект TCP-over-TCP під час втрат пакетів.
- `websocket-cover` змінює внутрішній потік і відповідає як звичайний сайт на
  probes, але не приховує destination IP, TLS
  fingerprint, загальний обсяг або timing і не гарантує обхід DPI.
- Packet reordering може бути помітний протоколам поверх UDP; TCP відновлює
  порядок власним sequence space.
- IPv6 назовні використовує NAT66, а не делегований клієнту глобальний IPv6 prefix.
- Це навчальна реалізація, а не production VPN із керуванням користувачами, ротацією ключів та kill switch.
