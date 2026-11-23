"""Read-only mitmweb content view for StellaSora handshake captures."""

import json
import os

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from mitmproxy import contentviews, http


def matches(metadata):
    flow = metadata.flow
    if not isinstance(flow, http.HTTPFlow) or metadata.http_message is None:
        return False
    request = flow.request
    return (
        request.method == "POST"
        and request.pretty_host.lower() in {"nova.yostar.cn", "localhost", "127.0.0.1", "::1"}
        and request.path.split("?", 1)[0] == "/agent-zone-1/"
        and request.headers.get("content-type", "").split(";", 1)[0].strip().lower()
        == "application/octet-stream"
    )


def varint(data, offset):
    value = 0
    for shift in range(0, 70, 7):
        byte = data[offset]
        offset += 1
        value |= (byte & 127) << shift
        if byte < 128:
            return value, offset
    raise ValueError("invalid protobuf varint")


def ike_request(data):
    fields = {}
    offset = 0
    while offset < len(data):
        tag, offset = varint(data, offset)
        number, wire_type = divmod(tag, 8)
        if wire_type == 0:
            value, offset = varint(data, offset)
        elif wire_type == 2:
            size, offset = varint(data, offset)
            if size > len(data) - offset:
                raise ValueError("truncated protobuf field")
            value = data[offset:offset + size]
            offset += size
        else:
            size = {1: 8, 5: 4}.get(wire_type)
            if size is None or size > len(data) - offset:
                raise ValueError("invalid protobuf field")
            value = data[offset:offset + size]
            offset += size
        fields[number] = value
    pubkey = fields.get(3)
    if not isinstance(pubkey, bytes) or len(pubkey) != 65 or pubkey[0] != 4:
        raise ValueError("unexpected IKE public key")
    result = {"PubKey": pubkey.hex()}
    for field, name in ((1, "ProtoVersion"), (2, "ClientTs")):
        if field in fields:
            if not isinstance(fields[field], int):
                raise ValueError(f"invalid {name}")
            result[name] = fields[field]
    if isinstance(fields.get(2047), bytes):
        result["NextPackageBytes"] = len(fields[2047])
    return result


def decrypt_request(data, key):
    if len(data) < 30:
        raise ValueError("truncated handshake frame")
    mask = len(data) & 255
    frame = bytearray()
    for index, byte in enumerate(data):
        value = byte ^ mask
        frame.append(((value >> 1) | ((value & 1) << 7)) ^ key[index % len(key)])
    nonce = bytes(frame[:12])
    plaintext = AESGCM(key).decrypt(nonce, bytes(frame[12:]), nonce)
    message_id = int.from_bytes(plaintext[:2], "big")
    if message_id != 1:
        raise ValueError(f"authenticated message {message_id} is outside the IKE request view")
    return {"message_id": message_id, "type": "proto.IKEReq", "body": ike_request(plaintext[2:])}


class StellaBody(contentviews.Contentview):
    name = "Stella Handshake / Ciphertext"
    syntax_highlight = "json"

    def render_priority(self, data: bytes, metadata: contentviews.Metadata) -> float:
        return 2 if data and matches(metadata) else 0

    def prettify(self, data: bytes, metadata: contentviews.Metadata) -> str:
        if not matches(metadata):
            return json.dumps({"status": "not a supported Stella HTTP flow"})
        if isinstance(metadata.http_message, http.Response):
            return json.dumps({
                "direction": "response",
                "bytes": len(data),
                "ciphertext_hex": data.hex(" "),
            }, indent=2)

        result = {"direction": "request", "bytes": len(data)}
        key = os.environ.get("STELLA_HANDSHAKE_KEY", "").encode("utf-8")
        if len(key) != 32:
            result["status"] = "Set STELLA_HANDSHAKE_KEY to the capture environment's 32-byte handshake key."
        else:
            try:
                result.update(decrypt_request(data, key))
                result["status"] = "authenticated"
            except InvalidTag:
                result["status"] = "Handshake authentication failed. This may be session traffic."
            except (ValueError, IndexError) as error:
                result["status"] = str(error)
        return json.dumps(result, indent=2)


contentviews.add(StellaBody)
