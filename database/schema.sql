-- SQL Bank - Phase 1 schema
-- Run with: sudo su - postgres -c "psql -f database/schema.sql" (after CREATE DATABASE bank)

CREATE TABLE users (
    user_id SERIAL PRIMARY KEY,
    username VARCHAR(50) UNIQUE NOT NULL,
    email VARCHAR(100) UNIQUE NOT NULL,
    password_hash VARCHAR(255) NOT NULL,
    created_at TIMESTAMP DEFAULT NOW()
);

CREATE TABLE accounts (
    account_id SERIAL PRIMARY KEY,
    user_id INT NOT NULL REFERENCES users(user_id),
    account_type VARCHAR(20) NOT NULL DEFAULT 'checking',
    balance NUMERIC(15, 2) NOT NULL DEFAULT 0.00,
    created_at TIMESTAMP DEFAULT NOW()
);

CREATE TABLE transactions (
    transaction_id SERIAL PRIMARY KEY,
    account_id INT NOT NULL REFERENCES accounts(account_id),
    transaction_type VARCHAR(20) NOT NULL,
    amount NUMERIC(15, 2) NOT NULL,
    fee NUMERIC(15, 2) NOT NULL DEFAULT 0.00,
    created_at TIMESTAMP DEFAULT NOW()
);